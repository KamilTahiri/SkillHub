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
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
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

    public static async Task<(bool Success, string Content)> ReadTextWithRetryAsync(string path, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(path))
            {
                return (false, string.Empty);
            }

            try
            {
                await using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    useAsync: true);

                if (fs.Length == 0)
                {
                    // File might be mid-truncation by an external editor; retry briefly
                    if (attempt < BackoffDelaysMs.Length)
                    {
                        await Task.Delay(BackoffDelaysMs[attempt], ct).ConfigureAwait(false);
                        continue;
                    }

                    return (false, string.Empty);
                }

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                return (true, ContentHasher.NormalizeLineEndings(text));
            }
            catch (FileNotFoundException)
            {
                return (false, string.Empty);
            }
            catch (DirectoryNotFoundException)
            {
                return (false, string.Empty);
            }
            catch (IOException) when (attempt < BackoffDelaysMs.Length)
            {
                await Task.Delay(BackoffDelaysMs[attempt], ct).ConfigureAwait(false);
            }
        }
    }
}

