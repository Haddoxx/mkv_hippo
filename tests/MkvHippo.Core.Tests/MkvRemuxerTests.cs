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
    public async Task RemuxAsyncMuxesToAWorkingFileThenMovesItOntoTheDestination()
    {
        string? reportedWorkingPath = null;
        var runner = WritingRunner("MUXED");
        var remuxer = new MkvRemuxer(runner);
        var plan = PlanFor("movie_multilang.json", "eng", "none");
        var output = Path.Combine(_tempDir, "sub", "out.mkv");

        var result = await remuxer.RemuxAsync("mkvmerge", plan, "in.mkv", output, CancellationToken.None,
            working => reportedWorkingPath = working);

        Assert.Equal(RemuxStatus.Ok, result.Status);
        var call = Assert.Single(runner.Calls);

        // mkvmerge is pointed at the working file, never at the destination itself.
        var muxTarget = call.Arguments[call.Arguments.ToList().IndexOf("-o") + 1];
        Assert.NotEqual(output, muxTarget);
        Assert.EndsWith(MkvRemuxer.WorkingSuffix, muxTarget);
        Assert.Equal(Path.GetDirectoryName(output), Path.GetDirectoryName(muxTarget));
        Assert.Equal(muxTarget, reportedWorkingPath);
        Assert.Equal(new[] { "--quiet", "-o", muxTarget, "-a", "1", "--no-subtitles", "in.mkv" }, call.Arguments);

        // …and only the destination survives.
        Assert.Equal("MUXED", File.ReadAllText(output));
        Assert.Empty(WorkingFiles(Path.GetDirectoryName(output)!));
    }

    [Fact]
    public async Task FailureLeavesAPreExistingDestinationIntact()
    {
        var output = TempFile("keepme.mkv", "PRECIOUS");
        var runner = WritingRunner("HALF-WRITTEN", exitCode: 2, stdErr: "Error: the muxing went wrong\n");
        var remuxer = new MkvRemuxer(runner);
        var plan = PlanFor("movie_multilang.json", "eng", "");

        var result = await remuxer.RemuxAsync("mkvmerge", plan, "in.mkv", output, CancellationToken.None);

        Assert.Equal(RemuxStatus.Failed, result.Status);
        Assert.Equal("PRECIOUS", File.ReadAllText(output));
        Assert.Empty(WorkingFiles(_tempDir));
    }

    [Fact]
    public async Task CancellationLeavesAPreExistingDestinationIntactAndPropagates()
    {
        var output = TempFile("keepme.mkv", "PRECIOUS");
        var runner = new FakeProcessRunner
        {
            Handler = (_, args, _) =>
            {
                // mkvmerge got as far as creating its working file before the kill landed.
                File.WriteAllText(args[args.ToList().IndexOf("-o") + 1], "HALF-WRITTEN");
                throw new OperationCanceledException();
            },
        };
        var remuxer = new MkvRemuxer(runner);
        var plan = PlanFor("movie_multilang.json", "eng", "");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => remuxer.RemuxAsync("mkvmerge", plan, "in.mkv", output, CancellationToken.None));

        Assert.Equal("PRECIOUS", File.ReadAllText(output));
        Assert.Empty(WorkingFiles(_tempDir));
    }

    [Fact]
    public async Task SuccessReplacesAPreExistingDestination()
    {
        var output = TempFile("replaceme.mkv", "OLD");
        var remuxer = new MkvRemuxer(WritingRunner("NEW"));
        var plan = PlanFor("movie_multilang.json", "eng", "");

        var result = await remuxer.RemuxAsync("mkvmerge", plan, "in.mkv", output, CancellationToken.None);

        Assert.Equal(RemuxStatus.Ok, result.Status);
        Assert.Equal("NEW", File.ReadAllText(output));
        Assert.Empty(WorkingFiles(_tempDir));
    }

    [Fact]
    public async Task WarningsStillCommitTheWorkingFile()
    {
        var output = Path.Combine(_tempDir, "warned.mkv");
        var remuxer = new MkvRemuxer(WritingRunner("MUXED", exitCode: 1, stdErr: "Warning: cues are missing\n"));
        var plan = PlanFor("movie_multilang.json", "eng", "");

        var result = await remuxer.RemuxAsync("mkvmerge", plan, "in.mkv", output, CancellationToken.None);

        Assert.Equal(RemuxStatus.OkWithWarnings, result.Status);
        Assert.Contains("cues are missing", result.Warnings);
        Assert.Equal("MUXED", File.ReadAllText(output));
        Assert.Empty(WorkingFiles(_tempDir));
    }

    [Fact]
    public void WorkingPathsAreUniquePerCallAndSitNextToTheDestination()
    {
        var output = Path.Combine(_tempDir, "out.mkv");
        var first = MkvRemuxer.WorkingPathFor(output);
        var second = MkvRemuxer.WorkingPathFor(output);

        Assert.NotEqual(first, second);
        Assert.Equal(_tempDir, Path.GetDirectoryName(first));
        Assert.EndsWith(MkvRemuxer.WorkingSuffix, first);
    }

    // --- Sweeping leftovers from an interrupted run ---

    [Fact]
    public void SweepRemovesOnlyWorkingFilesAndRecursesTheOutputTree()
    {
        var nested = Path.Combine(_tempDir, "shows", "s01");
        Directory.CreateDirectory(nested);
        var stale = Path.Combine(_tempDir, "movie.mkv.abc12345" + MkvRemuxer.WorkingSuffix);
        var staleNested = Path.Combine(nested, "e01.mkv.def67890" + MkvRemuxer.WorkingSuffix);
        var keep = Path.Combine(_tempDir, "movie.mkv");
        var keepNested = Path.Combine(nested, "e01.mkv");
        foreach (var f in new[] { stale, staleNested, keep, keepNested })
            File.WriteAllText(f, "x");

        int swept = MkvRemuxer.SweepWorkingFiles(_tempDir);

        Assert.Equal(2, swept);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleNested));
        Assert.True(File.Exists(keep), "real outputs must survive the sweep");
        Assert.True(File.Exists(keepNested));
    }

    [Fact]
    public void SweepIsSilentWhenThereIsNothingToDoOrNoOutputTreeYet()
    {
        Assert.Equal(0, MkvRemuxer.SweepWorkingFiles(_tempDir));
        Assert.Equal(0, MkvRemuxer.SweepWorkingFiles(Path.Combine(_tempDir, "does-not-exist")));
    }

    /// <summary>Emulates mkvmerge writing its -o target, then exiting with the given code.</summary>
    private static FakeProcessRunner WritingRunner(string content, int exitCode = 0, string stdErr = "")
    {
        return new FakeProcessRunner
        {
            Handler = (_, args, _) =>
            {
                var target = args[args.ToList().IndexOf("-o") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, content);
                return Task.FromResult(new ProcessResult(exitCode, "", stdErr));
            },
        };
    }

    private static string[] WorkingFiles(string directory) =>
        Directory.GetFiles(directory, "*" + MkvRemuxer.WorkingSuffix);
}
