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
/// Exit code 0 = ok, 1 = completed with warnings (output kept), 2 = failure (partial
/// output deleted). Cancellation also deletes the partial output.
/// </summary>
public sealed class MkvRemuxer
{
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

    public async Task<RemuxResult> RemuxAsync(
        string mkvmergePath, TrackPlan plan, string inputPath, string outputPath, CancellationToken ct)
    {
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(mkvmergePath, BuildArguments(plan, inputPath, outputPath), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DeletePartialOutput(outputPath);
            throw;
        }

        return Interpret(result, outputPath);
    }

    public static RemuxResult Interpret(ProcessResult result, string outputPath)
    {
        switch (result.ExitCode)
        {
            case 0:
                return new RemuxResult(RemuxStatus.Ok, 0, null, null);
            case 1:
                return new RemuxResult(RemuxStatus.OkWithWarnings, 1, CombineOutput(result), null);
            default:
                DeletePartialOutput(outputPath);
                return new RemuxResult(RemuxStatus.Failed, result.ExitCode, null, CombineOutput(result));
        }
    }

    private static string CombineOutput(ProcessResult result)
    {
        var text = (result.StdErr + "\n" + result.StdOut).Trim();
        return text.Length > 0 ? text : $"mkvmerge exited with code {result.ExitCode}";
    }

    private static void DeletePartialOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
