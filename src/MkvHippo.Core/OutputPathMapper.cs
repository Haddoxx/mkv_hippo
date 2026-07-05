namespace MkvHippo.Core;

public static class OutputPathMapper
{
    /// <summary>
    /// Maps an input file to its output path, mirroring the relative subpath under the output
    /// root. mkvmerge only writes Matroska, so a non-.mkv input (e.g. .mp4) maps to a .mkv
    /// output name; inputs already named .mkv keep their file name byte-for-byte.
    /// </summary>
    public static string Map(string inputRoot, string outputRoot, string inputFile)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(inputRoot), Path.GetFullPath(inputFile));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"\"{inputFile}\" is not under the input root \"{inputRoot}\".");
        }
        var mapped = Path.GetFullPath(Path.Combine(outputRoot, relative));
        return Path.GetExtension(mapped).Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            ? mapped
            : Path.ChangeExtension(mapped, ".mkv");
    }

    /// <summary>True if <paramref name="child"/> equals or lies inside <paramref name="parent"/>.</summary>
    public static bool IsInsideOrEqual(string parent, string child)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return c.Equals(p, comparison) || c.StartsWith(p + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Refuses an output root that is the input root or lies inside it.</summary>
    public static void EnsureValidRoots(string inputRoot, string outputRoot)
    {
        if (IsInsideOrEqual(inputRoot, outputRoot))
            throw new ArgumentException("The output folder must not be inside (or equal to) the input folder.");
    }

    /// <summary>
    /// Windows-style collision avoidance: returns the path unchanged if it is free,
    /// otherwise "name (1).mkv", "name (2).mkv", … — the first that does not exist.
    /// <paramref name="isReserved"/> lets callers also veto names that are merely claimed
    /// (e.g. outputs of jobs still running that haven't created their file yet).
    /// </summary>
    public static string MakeUnique(string path, Func<string, bool>? isReserved = null)
    {
        bool taken(string p) => File.Exists(p) || (isReserved?.Invoke(p) ?? false);
        if (!taken(path))
            return path;
        var directory = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!taken(candidate))
                return candidate;
        }
    }

    /// <summary>Default output root: a sibling of the input named "&lt;input&gt;-hippo".</summary>
    public static string DefaultOutputRoot(string inputRoot)
    {
        var full = Path.GetFullPath(inputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full + "-hippo";
    }
}
