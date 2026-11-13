namespace SkillSync.Daemon.Core.Engine;

public static class SkillPaths
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    };

    public static IReadOnlyList<string> SupportingFolders { get; } =
        Array.AsReadOnly(new[] { "scripts", "resources", "references", "examples", "assets", "agents" });

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool IsSupportingFolder(string folder) => SupportingFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);

    public static string SupportingPath(string input)
    {
        var normalized = NormalizeRelative(input);
        var separator = normalized.IndexOf('/');
        if (separator < 0 || !IsSupportingFolder(normalized[..separator]))
        {
            throw new ArgumentException($"Supporting file '{input}' must be inside a supported bundle folder.");
        }

        return normalized;
    }

    public static string NormalizeRelative(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || Path.IsPathRooted(input))
        {
            throw new ArgumentException("A nonempty relative path is required.");
        }

        var normalized = input.Replace('\\', '/');
        foreach (var segment in normalized.Split('/'))
        {
            ValidateSegment(segment);
        }

        return normalized;
    }

    public static void ValidateSegment(string segment)
    {
        if (segment.Length is 0 or > 255 || segment is "." or ".." ||
            segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) ||
            ReservedNames.Contains(segment.Split('.')[0].TrimEnd()))
        {
            throw new ArgumentException($"Unsafe path segment '{segment}'.");
        }
    }

    public static string Resolve(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, NormalizeRelative(relative)));
        var relation = Path.GetRelativePath(fullRoot, candidate);
        if (Path.IsPathRooted(relation) || relation == "." || relation == ".." ||
            relation.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must be strictly inside the configured directory.");
        }

        AssertNoReparsePoints(candidate);
        return candidate;
    }

    public static void AssertNoReparsePoints(string path)
    {
        var ancestors = new Stack<string>();
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            ancestors.Push(current);
        }
        foreach (var current in ancestors)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"SkillHub cannot access reparse point '{current}'.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    public static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
