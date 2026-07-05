using System.Diagnostics;
using MkvHippo.Core.Mkv;
using MkvHippo.Core.Processes;
using MkvHippo.Core.Reporting;
using MkvHippo.Core.Scheduling;

namespace MkvHippo.Core;

public enum OverwriteBehavior
{
    /// <summary>Never clobber an existing destination file; write "name (1).mkv" instead.</summary>
    AutoRename,
    /// <summary>Replace an existing destination file.</summary>
    Overwrite,
}

public sealed class BatchOptions
{
    public required string InputRoot { get; init; }
    public required string OutputRoot { get; init; }
    public required string MkvmergePath { get; init; }
    public required PlanOptions Plan { get; init; }
    public OverwriteBehavior Overwrite { get; init; } = OverwriteBehavior.AutoRename;
}

public sealed record ScanGroup(string Signature, IReadOnlyList<MkvTrack> Tracks, IReadOnlyList<string> Files);

public sealed record ScanReport(
    int TotalFiles,
    IReadOnlyList<ScanGroup> Groups,
    IReadOnlyList<FileResult> Failures,
    bool WasCancelled);

/// <summary>
/// Scan → plan → schedule → report. Sources are never modified: output is always written
/// to a mirrored path under the output root, which must lie outside the input root.
/// </summary>
public sealed class BatchRunner
{
    private readonly IProcessRunner _runner;

    public BatchRunner(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();

    /// <summary>Extensions picked up by a scan or run. Non-MKV containers are remuxed to .mkv.</summary>
    private static readonly string[] SourceExtensions = { ".mkv", ".mp4", ".m4v" };

    public static IReadOnlyList<string> FindSourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        })
        .Where(p => SourceExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public async Task<BatchSummary> RunAsync(
        BatchOptions options,
        AdaptiveScheduler scheduler,
        IProgress<ProgressEvent>? progress,
        CancellationToken ct)
    {
        OutputPathMapper.EnsureValidRoots(options.InputRoot, options.OutputRoot);

        var stopwatch = Stopwatch.StartNew();
        var files = FindSourceFiles(options.InputRoot);
        progress?.Report(new BatchStartedEvent(files.Count));

        var identifier = new MkvIdentifier(_runner);
        var remuxer = new MkvRemuxer(_runner);
        var results = new List<FileResult>();
        var matchedAudioTokens = new HashSet<string>();
        var matchedSubtitleTokens = new HashSet<string>();
        int processed = 0;

        // Distinct inputs can map to the same output name ("movie.mkv" + "movie.mp4" both
        // yield "movie.mkv"), and parallel jobs may resolve before either file exists on
        // disk — so auto-rename must also avoid names claimed by still-running jobs.
        var reservedOutputs = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string ResolveOutput(string mapped)
        {
            if (options.Overwrite != OverwriteBehavior.AutoRename)
                return mapped;
            lock (reservedOutputs)
            {
                var unique = OutputPathMapper.MakeUnique(mapped, reservedOutputs.Contains);
                reservedOutputs.Add(unique);
                return unique;
            }
        }

        void AccumulateMatches(MkvFileInfo info)
        {
            lock (matchedAudioTokens)
            {
                matchedAudioTokens.UnionWith(
                    TrackPlan.MatchedTokens(info.AudioTracks, options.Plan.Audio, options.Plan.Mode));
                matchedSubtitleTokens.UnionWith(
                    TrackPlan.MatchedTokens(info.SubtitleTracks, options.Plan.Subtitles, options.Plan.Mode));
            }
        }

        foreach (var file in files)
        {
            scheduler.Enqueue(async jobCt =>
            {
                progress?.Report(new FileStartedEvent(file, files.Count));
                FileResult result;
                try
                {
                    result = await ProcessOneAsync(options, identifier, remuxer, file, AccumulateMatches,
                        ResolveOutput,
                        output => progress?.Report(new FileOutputResolvedEvent(file, output)), jobCt)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result = new FileResult(file, FileOutcome.Cancelled, "cancelled");
                }
                catch (Exception ex)
                {
                    result = new FileResult(file, FileOutcome.Failed, ex.Message);
                }

                int done;
                lock (results)
                {
                    results.Add(result);
                    done = ++processed;
                }
                progress?.Report(new FileFinishedEvent(result, done, files.Count));
            });
        }

        await scheduler.WaitForIdleAsync().ConfigureAwait(false);
        stopwatch.Stop();

        lock (results)
        {
            // Files cancelled before their job ever ran never produced a result; count them too.
            int neverStarted = files.Count - results.Count;
            lock (matchedAudioTokens)
            {
                return new BatchSummary(
                    TotalFiles: files.Count,
                    Succeeded: results.Count(r => r.Succeeded),
                    Skipped: results.Count(r => r.Skipped),
                    Failed: results.Count(r => r.Outcome == FileOutcome.Failed),
                    Cancelled: results.Count(r => r.Outcome == FileOutcome.Cancelled) + neverStarted,
                    BytesIn: results.Where(r => r.Succeeded).Sum(r => r.BytesIn),
                    BytesOut: results.Where(r => r.Succeeded).Sum(r => r.BytesOut),
                    Elapsed: stopwatch.Elapsed,
                    WasCancelled: ct.IsCancellationRequested,
                    UnmatchedAudioTokens: UnmatchedTokens(options.Plan.Audio, matchedAudioTokens),
                    UnmatchedSubtitleTokens: UnmatchedTokens(options.Plan.Subtitles, matchedSubtitleTokens));
            }
        }
    }

    private static IReadOnlyList<string> UnmatchedTokens(TrackFilter filter, HashSet<string> matched) =>
        filter.KeepAll || filter.DropAll
            ? Array.Empty<string>()
            : filter.Tokens.Except(matched).OrderBy(t => t, StringComparer.Ordinal).ToList();

    private static async Task<FileResult> ProcessOneAsync(
        BatchOptions options, MkvIdentifier identifier, MkvRemuxer remuxer, string file,
        Action<MkvFileInfo> onIdentified, Func<string, string> resolveOutput,
        Action<string> onOutputResolved, CancellationToken ct)
    {
        var info = await identifier.IdentifyAsync(options.MkvmergePath, file, ct).ConfigureAwait(false);
        onIdentified(info);
        var plan = TrackPlan.Create(info, options.Plan);

        switch (plan.Action)
        {
            case PlanAction.SkipAlreadyClean:
                return new FileResult(file, FileOutcome.SkippedClean, "already clean");

            case PlanAction.SkipNoMatch:
                return new FileResult(file, FileOutcome.SkippedNoMatch, plan.Warning);

            default:
                var outputPath = resolveOutput(OutputPathMapper.Map(options.InputRoot, options.OutputRoot, file));
                onOutputResolved(outputPath);
                var remux = await remuxer.RemuxAsync(options.MkvmergePath, plan, file, outputPath, ct)
                    .ConfigureAwait(false);

                if (remux.Status == RemuxStatus.Failed)
                    return new FileResult(file, FileOutcome.Failed, remux.Error);

                long bytesIn = new FileInfo(file).Length;
                long bytesOut = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                return new FileResult(
                    file,
                    remux.Status == RemuxStatus.Ok ? FileOutcome.Ok : FileOutcome.OkWithWarnings,
                    remux.Warnings,
                    plan.RemovedAudio,
                    plan.RemovedSubtitles,
                    bytesIn,
                    bytesOut,
                    plan.KeptAudioSummary,
                    plan.KeptSubtitleSummary,
                    outputPath);
        }
    }

    /// <summary>Identify-only pass; groups files by identical track layout.</summary>
    public async Task<ScanReport> ScanAsync(
        string inputRoot,
        string mkvmergePath,
        AdaptiveScheduler scheduler,
        IProgress<ProgressEvent>? progress,
        CancellationToken ct)
    {
        var files = FindSourceFiles(inputRoot);
        progress?.Report(new BatchStartedEvent(files.Count));

        var identifier = new MkvIdentifier(_runner);
        var identified = new List<MkvFileInfo>();
        var failures = new List<FileResult>();
        int processed = 0;

        foreach (var file in files)
        {
            scheduler.Enqueue(async jobCt =>
            {
                string? error = null;
                try
                {
                    var info = await identifier.IdentifyAsync(mkvmergePath, file, jobCt).ConfigureAwait(false);
                    lock (identified)
                        identified.Add(info);
                }
                catch (OperationCanceledException)
                {
                    error = "cancelled";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                int done;
                lock (failures)
                {
                    if (error is not null)
                        failures.Add(new FileResult(file, error == "cancelled" ? FileOutcome.Cancelled : FileOutcome.Failed, error));
                    done = Interlocked.Increment(ref processed);
                }
                progress?.Report(new ScanFileEvent(file, error is null, error, done, files.Count));
            });
        }

        await scheduler.WaitForIdleAsync().ConfigureAwait(false);

        lock (identified)
        {
            var groups = identified
                .GroupBy(i => i.LayoutSignature)
                .Select(g => new ScanGroup(
                    g.Key,
                    g.First().Tracks,
                    g.Select(i => i.FilePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()))
                .OrderByDescending(g => g.Files.Count)
                .ToList();
            lock (failures)
                return new ScanReport(files.Count, groups, failures.ToList(), ct.IsCancellationRequested);
        }
    }
}
