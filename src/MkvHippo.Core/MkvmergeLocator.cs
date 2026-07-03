namespace MkvHippo.Core;

/// <summary>
/// Finds mkvmerge: (1) next to the application, (2) on PATH,
/// (3) the default MKVToolNix install location.
/// </summary>
public static class MkvmergeLocator
{
    public const string InstallHint =
        "mkvmerge was not found. Install MKVToolNix (https://mkvtoolnix.download/) or place " +
        "mkvmerge.exe next to MKVHippo.exe.";

    public static string? Locate() =>
        Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"), File.Exists);

    public static string? Locate(
        string appDirectory, string? pathEnvironment, Func<string, bool> fileExists, bool? windows = null)
    {
        bool isWindows = windows ?? OperatingSystem.IsWindows();
        string exeName = isWindows ? "mkvmerge.exe" : "mkvmerge";

        var candidates = new List<string> { Path.Combine(appDirectory, exeName) };

        if (!string.IsNullOrEmpty(pathEnvironment))
        {
            foreach (var dir in pathEnvironment.Split(
                Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(Path.Combine(dir, exeName));
            }
        }

        if (isWindows)
            candidates.Add(@"C:\Program Files\MKVToolNix\mkvmerge.exe");

        return candidates.FirstOrDefault(fileExists);
    }
}
