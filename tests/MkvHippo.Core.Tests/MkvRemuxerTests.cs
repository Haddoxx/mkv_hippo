using MkvHippo.Core.Mkv;
using MkvHippo.Core.Processes;

namespace MkvHippo.Core.Tests;

public class MkvRemuxerTests : IDisposable
{
    private readonly string _tempDir;

    public MkvRemuxerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mkvhippo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TempFile(string name, string content = "partial")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static TrackPlan PlanFor(string fixture, string audio, string subs) =>
        TrackPlan.Create(Fixtures.Parse(fixture), PlanOptions.FromText(FilterMode.Languages, audio, subs));

    // --- Argument building ---

    [Fact]
    public void KeepAllTracksProducesNoSelectionFlags()
    {
        var plan = PlanFor("movie_multilang.json", "", "");
        var args = MkvRemuxer.BuildArguments(plan, "in.mkv", "out.mkv");
        Assert.Equal(new[] { "--quiet", "-o", "out.mkv", "in.mkv" }, args);
    }

    [Fact]
    public void SubsetSelectionUsesTrackIdLists()
    {
        var plan = PlanFor("movie_multilang.json", "eng, jpn", "eng");
        var args = MkvRemuxer.BuildArguments(plan, "in.mkv", "out.mkv");
        Assert.Equal(new[] { "--quiet", "-o", "out.mkv", "-a", "1,2", "-s", "4", "in.mkv" }, args);
    }

    [Fact]
    public void DropAllUsesNoAudioAndNoSubtitles()
    {
        var plan = PlanFor("movie_multilang.json", "none", "none");
        var args = MkvRemuxer.BuildArguments(plan, "in.mkv", "out.mkv");
        Assert.Equal(new[] { "--quiet", "-o", "out.mkv", "--no-audio", "--no-subtitles", "in.mkv" }, args);
    }

    [Fact]
    public void MixedSelectionOnlyEmitsFlagsForFilteredTypes()
    {
        var plan = PlanFor("movie_multilang.json", "", "none");
        var args = MkvRemuxer.BuildArguments(plan, "in.mkv", "out.mkv");
        Assert.Equal(new[] { "--quiet", "-o", "out.mkv", "--no-subtitles", "in.mkv" }, args);
    }

    // --- Exit-code interpretation ---

    [Fact]
    public void ExitCodeZeroIsSuccess()
    {
        var result = MkvRemuxer.Interpret(new ProcessResult(0, "", ""), "unused.mkv");
        Assert.Equal(RemuxStatus.Ok, result.Status);
        Assert.Null(result.Warnings);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ExitCodeOneIsSuccessWithWarningsAndKeepsTheOutput()
    {
        var output = TempFile("warned.mkv");
        var result = MkvRemuxer.Interpret(
            new ProcessResult(1, "Warning: cues are missing\n", ""), output);

        Assert.Equal(RemuxStatus.OkWithWarnings, result.Status);
        Assert.Contains("cues are missing", result.Warnings);
        Assert.True(File.Exists(output), "exit code 1 must keep the output file");
    }

    [Fact]
    public void ExitCodeTwoIsFailureAndDeletesPartialOutput()
    {
        var output = TempFile("failed.mkv");
        var result = MkvRemuxer.Interpret(
            new ProcessResult(2, "", "Error: invalid track ID\n"), output);

        Assert.Equal(RemuxStatus.Failed, result.Status);
        Assert.Contains("invalid track ID", result.Error);
        Assert.False(File.Exists(output), "exit code 2 must delete the partial output file");
    }

    // --- RemuxAsync behaviour ---

    [Fact]
    public async Task RemuxAsyncPassesPlanArgumentsToMkvmerge()
    {
        var runner = new FakeProcessRunner();
        var remuxer = new MkvRemuxer(runner);
        var plan = PlanFor("movie_multilang.json", "eng", "none");
        var output = Path.Combine(_tempDir, "sub", "out.mkv");

        var result = await remuxer.RemuxAsync("mkvmerge", plan, "in.mkv", output, CancellationToken.None);

        Assert.Equal(RemuxStatus.Ok, result.Status);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "--quiet", "-o", output, "-a", "1", "--no-subtitles", "in.mkv" }, call.Arguments);
        Assert.True(Directory.Exists(Path.GetDirectoryName(output)), "output directory must be created");
    }

    [Fact]
    public async Task CancellationDeletesPartialOutputAndPropagates()
    {
        var output = TempFile("cancelled.mkv");
        var runner = new FakeProcessRunner
        {
            Handler = (_, _, _) => throw new OperationCanceledException(),
        };
        var remuxer = new MkvRemuxer(runner);
        var plan = PlanFor("movie_multilang.json", "eng", "");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => remuxer.RemuxAsync("mkvmerge", plan, "in.mkv", output, CancellationToken.None));
        Assert.False(File.Exists(output), "cancellation must delete the partial output file");
    }
}
