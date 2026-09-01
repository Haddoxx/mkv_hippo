using System.Text.Json;
using MkvHippo.Core.Processes;

namespace MkvHippo.Core.Mkv;

public enum MkvTrackType
{
    Video,
    Audio,
    Subtitles,
    Other,
}

public sealed record MkvTrack(
    int Id,
    MkvTrackType Type,
    string? Language,
    string? LanguageIetf,
    string? Codec = null,
    string? TrackName = null)
{
    /// <summary>
    /// Language tokens this track can be matched by, lowercase. A track with no
    /// language information yields the single token "und".
    /// </summary>
    public IReadOnlyList<string> LanguageTokens
    {
        get
        {
            var tokens = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Language))
                tokens.Add(Language.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(LanguageIetf))
                tokens.Add(LanguageIetf.Trim().ToLowerInvariant());
            if (tokens.Count == 0)
                tokens.Add("und");
            return tokens;
        }
    }

    /// <summary>
    /// Human-readable language: both forms when the IETF tag and the legacy
    /// three-letter code differ (e.g. "en / eng"), so users see every value a
    /// language filter can match.
    /// </summary>
    public string DisplayLanguage
    {
        get
        {
            var ietf = string.IsNullOrWhiteSpace(LanguageIetf) ? null : LanguageIetf.Trim();
            var legacy = string.IsNullOrWhiteSpace(Language) ? null : Language.Trim();
            if (ietf is null && legacy is null)
                return "und";
            if (ietf is null)
                return legacy!;
            if (legacy is null || string.Equals(ietf, legacy, StringComparison.OrdinalIgnoreCase))
                return ietf;
            return $"{ietf} / {legacy}";
        }
    }

    /// <summary>
    /// Grouping signature for scan reports. Includes the codec because the report prints it:
    /// without it, files whose IDs and languages line up but whose codecs differ (AAC vs DTS)
    /// would share a group and all be shown under the first file's codec.
    /// </summary>
    public string LayoutKey => $"{Id}:{Type}:{Language ?? ""}:{LanguageIetf ?? ""}:{Codec ?? ""}";
}

public sealed record MkvFileInfo(string FilePath, IReadOnlyList<MkvTrack> Tracks, string? ContainerType = null)
{
    public IReadOnlyList<MkvTrack> AudioTracks => Tracks.Where(t => t.Type == MkvTrackType.Audio).ToList();
    public IReadOnlyList<MkvTrack> SubtitleTracks => Tracks.Where(t => t.Type == MkvTrackType.Subtitles).ToList();

    /// <summary>
    /// True when the source container is affirmatively known not to be Matroska (e.g.
    /// "QuickTime/MP4"): mkvmerge only writes Matroska, so even a file whose tracks are
    /// all kept must be remuxed to become the .mkv output.
    /// </summary>
    public bool NeedsContainerConversion =>
        ContainerType is not null && !ContainerType.Equals("Matroska", StringComparison.OrdinalIgnoreCase);

    /// <summary>Signature of the full track layout, for grouping files in scan reports.</summary>
    public string LayoutSignature =>
        string.Join(" | ", Tracks.OrderBy(t => t.Id).Select(t => t.LayoutKey));
}

public sealed class MkvIdentifyException : Exception
{
    public MkvIdentifyException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Runs `mkvmerge -J` and parses its JSON identification output.</summary>
public sealed class MkvIdentifier
{
    private readonly IProcessRunner _runner;

    public MkvIdentifier(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();

    public async Task<MkvFileInfo> IdentifyAsync(string mkvmergePath, string filePath, CancellationToken ct)
    {
        var result = await _runner.RunAsync(mkvmergePath, new[] { "-J", filePath }, ct).ConfigureAwait(false);
        if (result.ExitCode == 2)
            throw new MkvIdentifyException(
                $"mkvmerge could not identify \"{filePath}\": {FirstLine(result.StdErr, result.StdOut)}");
        try
        {
            return Parse(filePath, result.StdOut);
        }
        catch (JsonException ex)
        {
            throw new MkvIdentifyException($"mkvmerge -J produced unparseable JSON for \"{filePath}\"", ex);
        }
    }

    public static MkvFileInfo Parse(string filePath, string identifyJson)
    {
        using var doc = JsonDocument.Parse(identifyJson);
        var root = doc.RootElement;
        var tracks = new List<MkvTrack>();

        string? containerType = null;
        if (root.TryGetProperty("container", out var container) && container.ValueKind == JsonValueKind.Object
            && container.TryGetProperty("type", out var containerTypeEl))
        {
            containerType = containerTypeEl.GetString();
        }

        if (root.TryGetProperty("tracks", out var tracksEl) && tracksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tracksEl.EnumerateArray())
            {
                int id = t.GetProperty("id").GetInt32();
                var type = t.GetProperty("type").GetString() switch
                {
                    "video" => MkvTrackType.Video,
                    "audio" => MkvTrackType.Audio,
                    "subtitles" => MkvTrackType.Subtitles,
                    _ => MkvTrackType.Other,
                };

                string? language = null, languageIetf = null, trackName = null;
                if (t.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    if (props.TryGetProperty("language", out var lang))
                        language = lang.GetString();
                    if (props.TryGetProperty("language_ietf", out var ietf))
                        languageIetf = ietf.GetString();
                    if (props.TryGetProperty("track_name", out var name))
                        trackName = name.GetString();
                }

                string? codec = t.TryGetProperty("codec", out var codecEl) ? codecEl.GetString() : null;
                tracks.Add(new MkvTrack(id, type, language, languageIetf, codec, trackName));
            }
        }

        return new MkvFileInfo(filePath, tracks, containerType);
    }

    private static string FirstLine(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var line = c.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is not null)
                return line;
        }
        return "(no output)";
    }
}
