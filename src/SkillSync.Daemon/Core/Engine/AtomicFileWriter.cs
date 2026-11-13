using System.Text;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Engine;

public static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly int[] BackoffDelaysMs = [20, 40, 80, 160, 320];

    public static bool IsTemporaryOrIgnoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return true;
        }

        return fileName.EndsWith(".skillsync-tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("~", StringComparison.Ordinal)
            || fileName.EndsWith(".swp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".swo", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".#", StringComparison.Ordinal)
            || path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task WriteTextAtomicallyAsync(string targetPath, string content, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException($"Invalid target path: {targetPath}", nameof(targetPath));

        SkillPaths.AssertNoReparsePoints(targetPath);
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(targetPath);
        var tempPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.skillsync-tmp");
        var normalized = ContentHasher.NormalizeLineEndings(content);
        var bytes = Utf8NoBom.GetBytes(normalized);

        try
        {
            await using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    SkillPaths.AssertNoReparsePoints(targetPath);
                    File.Move(tempPath, targetPath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < BackoffDelaysMs.Length)
                {
                    await Task.Delay(BackoffDelaysMs[attempt], ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Ignore cleanup failure
                }
            }
        }
    }

}
