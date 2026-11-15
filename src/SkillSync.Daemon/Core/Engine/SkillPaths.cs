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

    // Binary formats never belong in a text bundle. Everything else is checked for UTF-8 text when it is read.
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".tif", ".tiff", ".pdf", ".zip", ".gz", ".tgz", ".tar", ".7z", ".rar",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".mov", ".wav", ".ogg",
        ".webm", ".jar", ".class", ".pyc", ".docx", ".xlsx", ".pptx", ".psd", ".sqlite", ".db", ".wasm"
    };

    // Dependency and cache folders are never part of a skill even when they sit inside its directory.
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "__pycache__", "venv", ".venv", "site-packages"
    };

    /// <summary>
    /// True for a file that belongs to a skill bundle: any safe relative path inside the skill folder, at any depth
    /// (<c>reference.md</c>, <c>rules/async-parallel.md</c>, <c>scripts/run.py</c>), except SKILL.md itself, hidden files and
    /// folders, dependency folders and binary formats.
    /// </summary>
    public static bool IsBundleFile(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        string normalized;
        try { normalized = NormalizeRelative(relative); }
        catch (ArgumentException) { return false; }
        var segments = normalized.Split('/');
        if (segments.Length == 1 && segments[0].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) return false;
        if (segments.Any(segment => segment.StartsWith('.')) || segments[..^1].Any(ExcludedFolders.Contains)) return false;
        return !BinaryExtensions.Contains(Path.GetExtension(segments[^1]));
    }

    /// <summary>Kept for callers that only consider files directly beside SKILL.md.</summary>
    public static bool IsRootSiblingFile(string name) => !name.Contains('/') && !name.Contains('\\') && IsBundleFile(name);

    public static string SupportingPath(string input)
    {
        var normalized = NormalizeRelative(input);
        if (!IsBundleFile(normalized))
        {
            throw new ArgumentException($"Supporting file '{input}' must be a non-hidden text file inside the skill folder.");
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
