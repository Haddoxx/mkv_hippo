using MkvHippo.Core.Mkv;

namespace MkvHippo.Core.Tests;

public class TrackPlanTests
{
    private static TrackPlan Plan(string fixture, FilterMode mode, string? audio, string? subs) =>
        TrackPlan.Create(Fixtures.Parse(fixture), PlanOptions.FromText(mode, audio, subs));

    // --- TrackFilter parsing ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , , ")]
    public void EmptyFilterKeepsAll(string? text)
    {
        var filter = TrackFilter.Parse(text);
        Assert.True(filter.KeepAll);
        Assert.False(filter.DropAll);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData("  None  ")]
    public void NoneKeywordDropsAll(string text)
    {
        var filter = TrackFilter.Parse(text);
        Assert.True(filter.DropAll);
        Assert.False(filter.KeepAll);
    }

    [Fact]
    public void TokensAreTrimmedAndLowercased()
    {
        var filter = TrackFilter.Parse(" ENG , jpn ,Fr ");
        Assert.Equal(new HashSet<string> { "eng", "jpn", "fr" }, filter.Tokens);
    }

    // --- Language mode ---

    [Fact]
    public void LanguageModeKeepsOnlyMatchingTracks()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "eng", "eng");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Equal(new[] { 1 }, plan.KeptAudioIds);
        Assert.Equal(new[] { 4 }, plan.KeptSubtitleIds);
        Assert.Equal(2, plan.RemovedAudio);
        Assert.Equal(1, plan.RemovedSubtitles);
        Assert.False(plan.KeepsAllAudio);
        Assert.False(plan.KeepsAllSubtitles);
    }

    [Fact]
    public void LanguageModeMatchesIetfTagsToo()
    {
        // "ja" only appears as language_ietf; "fr" likewise.
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "ja", "fr");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Equal(new[] { 2 }, plan.KeptAudioIds);
        Assert.Equal(new[] { 5 }, plan.KeptSubtitleIds);
    }

    [Fact]
    public void LanguageModeIsCaseInsensitive()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "ENG, JPN", "ENG");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Equal(new[] { 1, 2 }, plan.KeptAudioIds);
    }

    [Fact]
    public void MissingLanguageMatchesTheUndToken()
    {
        var plan = Plan("movie_no_language.json", FilterMode.Languages, "und", "und");

        // Every audio and subtitle track is und → everything is kept → already clean.
        Assert.Equal(PlanAction.SkipAlreadyClean, plan.Action);
    }

    [Fact]
    public void NoneDropsAllTracksOfThatType()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "none", "none");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Empty(plan.KeptAudioIds);
        Assert.Empty(plan.KeptSubtitleIds);
        Assert.Equal(3, plan.RemovedAudio);
        Assert.Equal(2, plan.RemovedSubtitles);
    }

    [Fact]
    public void EmptyFiltersKeepEverythingAndSkip()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "", "");
        Assert.Equal(PlanAction.SkipAlreadyClean, plan.Action);
    }

    [Fact]
    public void FileAlreadyMatchingFiltersIsSkippedAsClean()
    {
        // eng-only file, keep eng audio; file has no subtitles at all.
        var plan = Plan("movie_eng_only.json", FilterMode.Languages, "eng", "eng");
        Assert.Equal(PlanAction.SkipAlreadyClean, plan.Action);
    }

    [Fact]
    public void Mp4KeepingEveryTrackStillRemuxesForContainerConversion()
    {
        // Same keep-everything situation as SkipAlreadyClean, but the source is not
        // Matroska: the file must still be remuxed to become the .mkv output.
        var plan = Plan("movie_mp4.json", FilterMode.Languages, "", "");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.True(plan.KeepsAllAudio);
        Assert.True(plan.KeepsAllSubtitles);
        Assert.Equal(0, plan.RemovedAudio);
        Assert.Equal(0, plan.RemovedSubtitles);
    }

    [Fact]
    public void Mp4TracksAreFilteredLikeMkvTracks()
    {
        var plan = Plan("movie_mp4.json", FilterMode.Languages, "eng", "eng");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Equal(new[] { 1 }, plan.KeptAudioIds);
        Assert.Equal(new[] { 3 }, plan.KeptSubtitleIds);
        Assert.Equal(1, plan.RemovedAudio);
    }

    [Fact]
    public void ZeroMatchSafetySkipAppliesToMp4Too()
    {
        var plan = Plan("movie_mp4.json", FilterMode.Languages, "ger", "eng");
        Assert.Equal(PlanAction.SkipNoMatch, plan.Action);
        Assert.Contains("audio filter", plan.Warning);
    }

    [Fact]
    public void ZeroMatchAudioFilterSkipsWithWarning()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "ger", "eng");

        Assert.Equal(PlanAction.SkipNoMatch, plan.Action);
        Assert.Contains("audio filter", plan.Warning);
    }

    [Fact]
    public void ZeroMatchSubtitleFilterStillRemuxesAndOnlyAdvises()
    {
        // Dropping every subtitle is what "keep only kor" asks for, and the result is a usable
        // file — blocking here would throw away the audio filtering that did match.
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "eng", "kor");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Null(plan.Warning);
        Assert.Contains("subtitle filter", plan.Advisory);
        Assert.Empty(plan.KeptSubtitleIds);
        Assert.NotEmpty(plan.KeptAudioIds);
    }

    [Fact]
    public void ZeroMatchOnBothTypesSkipsForTheAudioReason()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "ger", "kor");

        Assert.Equal(PlanAction.SkipNoMatch, plan.Action);
        Assert.Contains("audio filter", plan.Warning);
    }

    [Fact]
    public void AMatchingSubtitleFilterLeavesNoAdvisory()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "eng", "eng");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Null(plan.Advisory);
    }

    [Fact]
    public void AFileWithNoSubtitlesAtAllGetsNoAdvisory()
    {
        // Vacuous, not a zero match: there was nothing for the filter to miss.
        var plan = Plan("movie_eng_only.json", FilterMode.Languages, "eng", "kor");
        Assert.Null(plan.Advisory);
    }

    [Fact]
    public void NoneFilterDoesNotTriggerZeroMatchSafety()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "none", "");
        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Null(plan.Warning);
    }

    [Fact]
    public void FilterOnTypeTheFileDoesNotHaveIsVacuouslySatisfied()
    {
        // eng-only fixture has no subtitle tracks; a subtitle filter that matches nothing is fine.
        var plan = Plan("movie_eng_only.json", FilterMode.Languages, "eng", "kor");
        Assert.Equal(PlanAction.SkipAlreadyClean, plan.Action);
    }

    // --- Kept-track summaries (for log lines) ---

    [Fact]
    public void KeptSummariesListLanguagesInLanguageMode()
    {
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "eng, jpn", "eng");
        Assert.Equal("eng,jpn", plan.KeptAudioSummary);
        Assert.Equal("eng", plan.KeptSubtitleSummary);
    }

    [Fact]
    public void KeptSummariesShowAllForKeepAllFilters()
    {
        // The tell-tale for an accidentally empty filter box.
        var plan = Plan("movie_multilang.json", FilterMode.Languages, "eng", "");
        Assert.Equal("eng", plan.KeptAudioSummary);
        Assert.Equal("all", plan.KeptSubtitleSummary);
    }

    [Fact]
    public void KeptSummariesShowNoneForDropAllAndDashForAbsentTypes()
    {
        var dropAll = Plan("movie_multilang.json", FilterMode.Languages, "none", "none");
        Assert.Equal("none", dropAll.KeptAudioSummary);
        Assert.Equal("none", dropAll.KeptSubtitleSummary);

        // eng-only fixture has no subtitle tracks at all.
        var noSubs = Plan("movie_eng_only.json", FilterMode.Languages, "eng", "eng");
        Assert.Equal("-", noSubs.KeptSubtitleSummary);
    }

    [Fact]
    public void KeptSummariesListIdsInTrackIdMode()
    {
        var plan = Plan("movie_multilang.json", FilterMode.TrackIds, "2, 1", "4");
        Assert.Equal("1,2", plan.KeptAudioSummary);
        Assert.Equal("4", plan.KeptSubtitleSummary);
    }

    // --- Matched-token reporting ---

    [Fact]
    public void MatchedTokensReportsWhichFilterValuesHit()
    {
        var file = Fixtures.Parse("movie_multilang.json");
        var filter = TrackFilter.Parse("eng, kor, ja");

        var matched = TrackPlan.MatchedTokens(file.AudioTracks, filter, FilterMode.Languages);

        Assert.Equal(new HashSet<string> { "eng", "ja" }, matched);
    }

    [Fact]
    public void MatchedTokensIsEmptyForKeepAllAndDropAllFilters()
    {
        var file = Fixtures.Parse("movie_multilang.json");
        Assert.Empty(TrackPlan.MatchedTokens(file.AudioTracks, TrackFilter.Parse(""), FilterMode.Languages));
        Assert.Empty(TrackPlan.MatchedTokens(file.AudioTracks, TrackFilter.Parse("none"), FilterMode.Languages));
    }

    [Fact]
    public void MatchedTokensWorksInTrackIdMode()
    {
        var file = Fixtures.Parse("movie_multilang.json");
        var filter = TrackFilter.Parse("1, 77");

        var matched = TrackPlan.MatchedTokens(file.AudioTracks, filter, FilterMode.TrackIds);

        Assert.Equal(new HashSet<string> { "1" }, matched);
    }

    // --- Track-ID mode ---

    [Fact]
    public void TrackIdModeKeepsListedIds()
    {
        var plan = Plan("movie_multilang.json", FilterMode.TrackIds, "1, 2", "4");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Equal(new[] { 1, 2 }, plan.KeptAudioIds);
        Assert.Equal(new[] { 4 }, plan.KeptSubtitleIds);
        Assert.Equal(1, plan.RemovedAudio);
        Assert.Equal(1, plan.RemovedSubtitles);
    }

    [Fact]
    public void TrackIdModeCoveringAllTracksSkipsAsClean()
    {
        var plan = Plan("movie_multilang.json", FilterMode.TrackIds, "1,2,3", "4,5");
        Assert.Equal(PlanAction.SkipAlreadyClean, plan.Action);
    }

    [Fact]
    public void TrackIdModeZeroMatchSkipsWithWarning()
    {
        var plan = Plan("movie_multilang.json", FilterMode.TrackIds, "77", "4");

        Assert.Equal(PlanAction.SkipNoMatch, plan.Action);
        Assert.Contains("audio filter", plan.Warning);
    }

    [Fact]
    public void TrackIdModeNoneAndEmptyBehaveLikeLanguageMode()
    {
        var plan = Plan("movie_multilang.json", FilterMode.TrackIds, "none", "");

        Assert.Equal(PlanAction.Remux, plan.Action);
        Assert.Empty(plan.KeptAudioIds);
        Assert.True(plan.KeepsAllSubtitles);
    }
}
