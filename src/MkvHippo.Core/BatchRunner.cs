using System.Diagnostics;
using MkvHippo.Core.Mkv;
using MkvHippo.Core.Processes;
using MkvHippo.Core.Reporting;
using MkvHippo.Core.Scheduling;

namespace MkvHippo.Core;

public sealed class BatchOptions
{
    public required string InputRoot { get; init; }
    public required string OutputRoot { get; init; }
    public required string MkvmergePath { get; init; }
    public required PlanOptions Plan { get; init; }
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

    public static IReadOnlyList<string> FindMkvFiles(string root) =>
        Directory.EnumerateFiles(root, "*.mkv", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true,
        })
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
        var files = FindMkvFiles(options.InputRoot);
        progress?.Report(new BatchStartedEvent(files.Count));

        var identifier = new MkvIdentifier(_runner);
        var remuxer = new MkvRemuxer(_runner);
        var results = new List<FileResult>();
        int processed = 0;

        foreach (var file in files)
        {
            scheduler.Enqueue(async jobCt =>
            {
                progress?.Report(new FileStartedEvent(file, files.Count));
                FileResult result;
                try
                {
                    result = await ProcessOneAsync(options, identifier, remuxer, file, jobCt).ConfigureAwait(false);
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
            return new BatchSummary(
                TotalFiles: files.Count,
                Succeeded: results.Count(r => r.Succeeded),
                Skipped: results.Count(r => r.Skipped),
                Failed: results.Count(r => r.Outcome == FileOutcome.Failed),
                Cancelled: results.Count(r => r.Outcome == FileOutcome.Cancelled) + neverStarted,
                BytesIn: results.Where(r => r.Succeeded).Sum(r => r.BytesIn),
                BytesOut: results.Where(r => r.Succeeded).Sum(r => r.BytesOut),
                Elapsed: stopwatch.Elapsed,
                WasCancelled: ct.IsCancellationRequested);
        }
    }

    private static async Task<FileResult> ProcessOneAsync(
        BatchOptions options, MkvIdentifier identifier, MkvRemuxer remuxer, string file, CancellationToken ct)
    {
        var info = await identifier.IdentifyAsync(options.MkvmergePath, file, ct).ConfigureAwait(false);
        var plan = TrackPlan.Create(info, options.Plan);

        switch (plan.Action)
        {
            case PlanAction.SkipAlreadyClean:
                return new FileResult(file, FileOutcome.SkippedClean, "already clean");

            case PlanAction.SkipNoMatch:
                return new FileResult(file, FileOutcome.SkippedNoMatch, plan.Warning);

            default:
                var outputPath = OutputPathMapper.Map(options.InputRoot, options.OutputRoot, file);
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
                    bytesOut);
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
        var files = FindMkvFiles(inputRoot);
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
