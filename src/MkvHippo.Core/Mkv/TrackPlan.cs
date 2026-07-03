using System.Globalization;

namespace MkvHippo.Core.Mkv;

public enum FilterMode
{
    Languages,
    TrackIds,
}

/// <summary>
/// A parsed keep-filter for one track type. Empty input keeps all tracks of the type,
/// the keyword "none" drops all of them, anything else is a comma-separated token list
/// (language codes or mkvmerge track IDs depending on <see cref="FilterMode"/>).
/// </summary>
public sealed class TrackFilter
{
    private TrackFilter(bool keepAll, bool dropAll, IReadOnlySet<string> tokens)
    {
        KeepAll = keepAll;
        DropAll = dropAll;
        Tokens = tokens;
    }

    public bool KeepAll { get; }
    public bool DropAll { get; }
    public IReadOnlySet<string> Tokens { get; }

    public static TrackFilter Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TrackFilter(keepAll: true, dropAll: false, new HashSet<string>());

        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return new TrackFilter(keepAll: false, dropAll: true, new HashSet<string>());

        var tokens = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();
        if (tokens.Count == 0)
            return new TrackFilter(keepAll: true, dropAll: false, tokens);
        return new TrackFilter(keepAll: false, dropAll: false, tokens);
    }
}

public sealed record PlanOptions(FilterMode Mode, TrackFilter Audio, TrackFilter Subtitles)
{
    public static PlanOptions FromText(FilterMode mode, string? audioFilter, string? subtitleFilter) =>
        new(mode, TrackFilter.Parse(audioFilter), TrackFilter.Parse(subtitleFilter));
}

public enum PlanAction
{
    /// <summary>Some tracks must be dropped; remux the file.</summary>
    Remux,
    /// <summary>Every existing audio and subtitle track is kept; nothing to do.</summary>
    SkipAlreadyClean,
    /// <summary>A filter matched zero tracks of a type the file has; skip with a warning.</summary>
    SkipNoMatch,
}

/// <summary>The keep/drop decision for a single file.</summary>
public sealed record TrackPlan(
    PlanAction Action,
    IReadOnlyList<int> KeptAudioIds,
    IReadOnlyList<int> KeptSubtitleIds,
    bool KeepsAllAudio,
    bool KeepsAllSubtitles,
    int RemovedAudio,
    int RemovedSubtitles,
    string? Warning,
    string KeptAudioSummary,
    string KeptSubtitleSummary)
{
    public static TrackPlan Create(MkvFileInfo file, PlanOptions options)
    {
        var audio = file.AudioTracks;
        var subtitles = file.SubtitleTracks;

        var keptAudio = SelectKept(audio, options.Audio, options.Mode);
        var keptSubtitles = SelectKept(subtitles, options.Subtitles, options.Mode);

        bool keepsAllAudio = keptAudio.Count == audio.Count;
        bool keepsAllSubtitles = keptSubtitles.Count == subtitles.Count;

        var warnings = new List<string>();
        if (IsZeroMatch(audio, keptAudio, options.Audio))
            warnings.Add("audio filter matches none of the file's audio tracks");
        if (IsZeroMatch(subtitles, keptSubtitles, options.Subtitles))
            warnings.Add("subtitle filter matches none of the file's subtitle tracks");

        var action =
            warnings.Count > 0 ? PlanAction.SkipNoMatch :
            keepsAllAudio && keepsAllSubtitles ? PlanAction.SkipAlreadyClean :
            PlanAction.Remux;

        return new TrackPlan(
            action,
            keptAudio.Select(t => t.Id).OrderBy(id => id).ToList(),
            keptSubtitles.Select(t => t.Id).OrderBy(id => id).ToList(),
            keepsAllAudio,
            keepsAllSubtitles,
            audio.Count - keptAudio.Count,
            subtitles.Count - keptSubtitles.Count,
            warnings.Count > 0 ? string.Join("; ", warnings) : null,
            SummarizeKept(keptAudio, audio.Count, options.Mode),
            SummarizeKept(keptSubtitles, subtitles.Count, options.Mode));
    }

    /// <summary>
    /// Compact description of what a plan keeps, for log lines: "all", "none",
    /// "-" (file has no tracks of the type), or the kept languages/IDs.
    /// An unexpected "all" is the tell-tale of an accidentally empty filter.
    /// </summary>
    private static string SummarizeKept(List<MkvTrack> kept, int totalOfType, FilterMode mode)
    {
        if (totalOfType == 0)
            return "-";
        if (kept.Count == totalOfType)
            return "all";
        if (kept.Count == 0)
            return "none";
        return mode == FilterMode.TrackIds
            ? string.Join(",", kept.Select(t => t.Id).OrderBy(id => id))
            : string.Join(",", kept.Select(t => t.LanguageTokens[0]));
    }

    private static List<MkvTrack> SelectKept(IReadOnlyList<MkvTrack> tracks, TrackFilter filter, FilterMode mode)
    {
        if (filter.KeepAll)
            return tracks.ToList();
        if (filter.DropAll)
            return new List<MkvTrack>();
        return tracks.Where(t => Matches(t, filter, mode)).ToList();
    }

    private static bool Matches(MkvTrack track, TrackFilter filter, FilterMode mode) =>
        filter.Tokens.Any(token => MatchesToken(track, token, mode));

    private static bool MatchesToken(MkvTrack track, string token, FilterMode mode) => mode switch
    {
        FilterMode.TrackIds => token == track.Id.ToString(CultureInfo.InvariantCulture),
        _ => track.LanguageTokens.Contains(token),
    };

    /// <summary>
    /// The filter tokens that match at least one of the given tracks. Batch code unions
    /// this across files to warn about filter values that never matched anything.
    /// </summary>
    public static IReadOnlySet<string> MatchedTokens(
        IEnumerable<MkvTrack> tracks, TrackFilter filter, FilterMode mode)
    {
        if (filter.KeepAll || filter.DropAll)
            return new HashSet<string>();
        var list = tracks as IReadOnlyList<MkvTrack> ?? tracks.ToList();
        return filter.Tokens.Where(token => list.Any(t => MatchesToken(t, token, mode))).ToHashSet();
    }

    private static bool IsZeroMatch(IReadOnlyList<MkvTrack> tracks, List<MkvTrack> kept, TrackFilter filter) =>
        !filter.KeepAll && !filter.DropAll && tracks.Count > 0 && kept.Count == 0;
}
