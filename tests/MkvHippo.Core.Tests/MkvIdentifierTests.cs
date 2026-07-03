using MkvHippo.Core.Mkv;
using MkvHippo.Core.Processes;

namespace MkvHippo.Core.Tests;

public class MkvIdentifierTests
{
    [Fact]
    public void ParsesMultiLanguageFixture()
    {
        var info = Fixtures.Parse("movie_multilang.json");

        Assert.Equal(6, info.Tracks.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, info.Tracks.Select(t => t.Id));

        Assert.Equal(MkvTrackType.Video, info.Tracks[0].Type);
        Assert.Equal(3, info.AudioTracks.Count);
        Assert.Equal(2, info.SubtitleTracks.Count);

        var jpn = info.Tracks.Single(t => t.Id == 2);
        Assert.Equal(MkvTrackType.Audio, jpn.Type);
        Assert.Equal("jpn", jpn.Language);
        Assert.Equal("ja", jpn.LanguageIetf);
        Assert.Equal("Japanese Stereo", jpn.TrackName);
        Assert.Equal("AAC", jpn.Codec);
    }

    [Fact]
    public void TracksWithoutLanguagePropertiesGetTheUndToken()
    {
        var info = Fixtures.Parse("movie_no_language.json");

        var audio = Assert.Single(info.AudioTracks);
        Assert.Null(audio.Language);
        Assert.Null(audio.LanguageIetf);
        Assert.Equal(new[] { "und" }, audio.LanguageTokens);

        var sub = Assert.Single(info.SubtitleTracks);
        Assert.Equal(new[] { "und" }, sub.LanguageTokens);
    }

    [Fact]
    public void ExplicitUndAndIetfTokensAreExposedLowercase()
    {
        var info = Fixtures.Parse("movie_exotic.json");

        var video = info.Tracks.Single(t => t.Id == 0);
        Assert.Contains("und", video.LanguageTokens);

        var audio = Assert.Single(info.AudioTracks);
        Assert.Equal(new[] { "eng", "en-us" }, audio.LanguageTokens);
    }

    [Fact]
    public void UnknownTrackTypesMapToOther()
    {
        var info = Fixtures.Parse("movie_exotic.json");

        var buttons = info.Tracks.Single(t => t.Id == 2);
        Assert.Equal(MkvTrackType.Other, buttons.Type);
        Assert.Empty(info.SubtitleTracks);
    }

    [Fact]
    public void LayoutSignatureGroupsIdenticalLayoutsAndSeparatesDifferentOnes()
    {
        var a = Fixtures.Parse("movie_multilang.json", "a.mkv");
        var b = Fixtures.Parse("movie_multilang.json", "b.mkv");
        var c = Fixtures.Parse("movie_eng_only.json", "c.mkv");

        Assert.Equal(a.LayoutSignature, b.LayoutSignature);
        Assert.NotEqual(a.LayoutSignature, c.LayoutSignature);
    }

    [Fact]
    public async Task IdentifyAsyncRunsMkvmergeWithJsonFlagAndParses()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _, _) => Task.FromResult(ProcessResultWithJson()),
        };
        var identifier = new MkvIdentifier(runner);

        var info = await identifier.IdentifyAsync("mkvmerge", "/media/movie.mkv", CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("mkvmerge", call.ExePath);
        Assert.Equal(new[] { "-J", "/media/movie.mkv" }, call.Arguments);
        Assert.Equal("/media/movie.mkv", info.FilePath);
        Assert.Equal(6, info.Tracks.Count);
    }

    private static ProcessResult ProcessResultWithJson() =>
        new(0, Fixtures.Json("movie_multilang.json"), "");

    [Fact]
    public async Task IdentifyAsyncThrowsOnExitCodeTwo()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _, _) => Task.FromResult(
                new ProcessResult(2, "", "mkvmerge: error: unsupported container\n")),
        };
        var identifier = new MkvIdentifier(runner);

        var ex = await Assert.ThrowsAsync<MkvIdentifyException>(
            () => identifier.IdentifyAsync("mkvmerge", "bad.mkv", CancellationToken.None));
        Assert.Contains("unsupported container", ex.Message);
    }

    [Fact]
    public async Task IdentifyAsyncThrowsOnUnparseableJson()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _, _) => Task.FromResult(new ProcessResult(0, "not json at all", "")),
        };
        var identifier = new MkvIdentifier(runner);

        await Assert.ThrowsAsync<MkvIdentifyException>(
            () => identifier.IdentifyAsync("mkvmerge", "weird.mkv", CancellationToken.None));
    }

    [Fact]
    public void ParseToleratesMissingTracksArray()
    {
        var info = MkvIdentifier.Parse("x.mkv", "{\"container\": {\"recognized\": true}}");
        Assert.Empty(info.Tracks);
    }
}
