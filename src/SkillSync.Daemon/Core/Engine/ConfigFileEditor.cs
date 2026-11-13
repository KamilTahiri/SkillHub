using System.Text;

namespace SkillSync.Daemon.Core.Engine;

internal static class ConfigFileEditor
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly object Gate = new();

    public static string Read(string path)
    {
        SkillPaths.AssertNoReparsePoints(path);
        if (!File.Exists(path))
        {
            return string.Empty;
        }
        if (new FileInfo(path).Length > 8 * 1024 * 1024)
        {
            throw new InvalidDataException("The client configuration exceeds the safe editing size.");
        }
        return Utf8.GetString(File.ReadAllBytes(path));
    }

    public static bool Edit(string path, Func<string, string> edit)
    {
        path = Path.GetFullPath(path);
        SkillPaths.ValidateSegment(Path.GetFileName(path));
        lock (Gate)
        {
            var original = Read(path);
            var existed = File.Exists(path);
            var bom = original.StartsWith('\uFEFF') ? "\uFEFF" : string.Empty;
            var updated = bom + edit(original[bom.Length..]);
            if (updated == original)
            {
                return false;
            }

            var directory = Path.GetDirectoryName(path)!;
            var backup = $"{path}.bak";
            SkillPaths.AssertNoReparsePoints(backup);
            Directory.CreateDirectory(directory);
            var temporary = SkillPaths.Resolve(directory, $".skillhub-config-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(Utf8.GetBytes(updated));
                    output.Flush(flushToDisk: true);
                }

                if (File.Exists(path) != existed || Read(path) != original)
                {
                    throw new SkillConflictException($"'{path}' changed while SkillHub was preparing the repair.");
                }
                if (existed)
                {
                    File.Replace(temporary, path, backup);
                }
                else
                {
                    File.Move(temporary, path);
                }
                return true;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
