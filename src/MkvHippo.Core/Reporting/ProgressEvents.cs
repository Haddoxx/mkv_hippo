namespace MkvHippo.Core.Reporting;

public enum FileOutcome
{
    Ok,
    OkWithWarnings,
    SkippedClean,
    SkippedNoMatch,
    /// <summary>Another source in the same batch already claimed this file's destination.</summary>
    SkippedCollision,
    Failed,
    Cancelled,
}

public sealed record FileResult(
    string InputPath,
    FileOutcome Outcome,
    string? Message = null,
    int RemovedAudio = 0,
    int RemovedSubtitles = 0,
    long BytesIn = 0,
    long BytesOut = 0,
    string? KeptAudio = null,
    string? KeptSubtitles = null,
    string? OutputPath = null)
{
    public bool Succeeded => Outcome is FileOutcome.Ok or FileOutcome.OkWithWarnings;
    public bool Skipped =>
        Outcome is FileOutcome.SkippedClean or FileOutcome.SkippedNoMatch or FileOutcome.SkippedCollision;
}

public abstract record ProgressEvent;

public sealed record BatchStartedEvent(int TotalFiles) : ProgressEvent;

public sealed record FileStartedEvent(string InputPath, int TotalFiles) : ProgressEvent;

/// <summary>
/// The destination for a remux is decided (after any auto-rename) and the mux is about to
/// start. <paramref name="WorkingPath"/> is the file actually growing on disk until the mux
/// succeeds and is moved onto <paramref name="OutputPath"/>.
/// </summary>
public sealed record FileOutputResolvedEvent(string InputPath, string OutputPath, string WorkingPath) : ProgressEvent;

public sealed record FileFinishedEvent(FileResult Result, int Processed, int TotalFiles) : ProgressEvent;

public sealed record ScanFileEvent(string InputPath, bool Identified, string? Error, int Processed, int TotalFiles) : ProgressEvent;

public sealed record BatchSummary(
    int TotalFiles,
    int Succeeded,
    int Skipped,
    int Failed,
    int Cancelled,
    long BytesIn,
    long BytesOut,
    TimeSpan Elapsed,
    bool WasCancelled,
    IReadOnlyList<string> UnmatchedAudioTokens,
    IReadOnlyList<string> UnmatchedSubtitleTokens);
