using MkvHippo.Core.Processes;

namespace MkvHippo.Core.Mkv;

public enum RemuxStatus
{
    Ok,
    OkWithWarnings,
    Failed,
}

public sealed record RemuxResult(RemuxStatus Status, int ExitCode, string? Warnings, string? Error);

/// <summary>
/// Drives `mkvmerge` to remux one file with the tracks a <see cref="TrackPlan"/> keeps.
/// Exit code 0 = ok, 1 = completed with warnings (output kept), 2 = failure.
///
/// The mux never writes to the requested destination directly: mkvmerge truncates its -o
/// target the moment it opens it, so a failure or a stop halfway through would destroy a
/// file that was already there. Each mux writes to a sibling working file and is moved onto
/// the destination only once mkvmerge has reported success; a failure or a cancellation
/// deletes that working file and leaves the destination exactly as it was.
/// </summary>
public sealed class MkvRemuxer
{
    /// <summary>Suffix of the in-progress mux file. Not one of the discovered source extensions.</summary>
    public const string WorkingSuffix = ".mkvhippo-tmp";

    private readonly IProcessRunner _runner;

    public MkvRemuxer(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();

    public static IReadOnlyList<string> BuildArguments(TrackPlan plan, string inputPath, string outputPath)
    {
        var args = new List<string> { "--quiet", "-o", outputPath };

        if (!plan.KeepsAllAudio)
        {
            if (plan.KeptAudioIds.Count == 0)
                args.Add("--no-audio");
            else
            {
                args.Add("-a");
                args.Add(string.Join(',', plan.KeptAudioIds));
            }
        }

        if (!plan.KeepsAllSubtitles)
        {
            if (plan.KeptSubtitleIds.Count == 0)
                args.Add("--no-subtitles");
            else
            {
                args.Add("-s");
                args.Add(string.Join(',', plan.KeptSubtitleIds));
            }
        }

        args.Add(inputPath);
        return args;
    }

    /// <summary>
    /// A unique in-progress path next to <paramref name="outputPath"/>, so the final move is a
    /// same-directory (and therefore same-volume, atomic) rename.
    /// </summary>
    public static string WorkingPathFor(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? "";
        var token = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(directory, $"{Path.GetFileName(outputPath)}.{token}{WorkingSuffix}");
    }

    /// <summary>
    /// Deletes working files left under <paramref name="root"/> by a run that was interrupted
    /// without cleanup — a crash, a power loss, a killed process. An ordinary failure or
    /// cancellation already removes its own. A file another instance is actively muxing is
    /// locked on Windows, so its delete fails and it is left alone.
    /// </summary>
    /// <returns>How many leftovers were removed.</returns>
    public static int SweepWorkingFiles(string root)
    {
        string[] leftovers;
        try
        {
            if (!Directory.Exists(root))
                return 0;
            leftovers = Directory.GetFiles(root, "*" + WorkingSuffix, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        int swept = 0;
        foreach (var path in leftovers)
        {
            try
            {
                File.Delete(path);
                swept++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return swept;
    }

    /// <param name="onWorkingPath">
    /// Called with the in-progress path once it is chosen, before mkvmerge starts — it is the
    /// file growing on disk while the mux runs, which is what a throughput gauge must measure.
    /// </param>
    public async Task<RemuxResult> RemuxAsync(
        string mkvmergePath, TrackPlan plan, string inputPath, string outputPath, CancellationToken ct,
        Action<string>? onWorkingPath = null)
    {
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var workingPath = WorkingPathFor(outputPath);
        onWorkingPath?.Invoke(workingPath);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(mkvmergePath, BuildArguments(plan, inputPath, workingPath), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(workingPath);
            throw;
        }

        // Interpret deletes the working file itself on failure; the destination is untouched.
        var interpreted = Interpret(result, workingPath);
        if (interpreted.Status == RemuxStatus.Failed)
            return interpreted;

        try
        {
            File.Move(workingPath, outputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(workingPath);
            return new RemuxResult(RemuxStatus.Failed, interpreted.ExitCode, null,
                $"muxed successfully but could not be moved to \"{outputPath}\": {ex.Message}");
        }

        return interpreted;
    }

    /// <param name="writtenPath">The file mkvmerge was writing — deleted if the mux failed.</param>
    public static RemuxResult Interpret(ProcessResult result, string writtenPath)
    {
        switch (result.ExitCode)
        {
            case 0:
                return new RemuxResult(RemuxStatus.Ok, 0, null, null);
            case 1:
                return new RemuxResult(RemuxStatus.OkWithWarnings, 1, CombineOutput(result), null);
            default:
                DeleteQuietly(writtenPath);
                return new RemuxResult(RemuxStatus.Failed, result.ExitCode, null, CombineOutput(result));
        }
    }

    private static string CombineOutput(ProcessResult result)
    {
        var text = (result.StdErr + "\n" + result.StdOut).Trim();
        return text.Length > 0 ? text : $"mkvmerge exited with code {result.ExitCode}";
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
