using MkvHippo.Core.Mkv;
using MkvHippo.Core.Processes;
using MkvHippo.Core.Reporting;
using MkvHippo.Core.Scheduling;

namespace MkvHippo.Core.Tests;

public class BatchRunnerTests : IDisposable
{
    private readonly string _root;
    private readonly string _inputRoot;
    private readonly string _outputRoot;

    public BatchRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mkvhippo-batch-" + Guid.NewGuid().ToString("N"));
        _inputRoot = Path.Combine(_root, "in");
        _outputRoot = Path.Combine(_root, "out");
        Directory.CreateDirectory(_inputRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string AddSourceFile(string relativePath, string content = "SOURCE-MKV-BYTES")
    {
        var path = Path.Combine(_inputRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Emulates mkvmerge: answers -J calls with a fixture chosen by file name and
    /// answers remux calls by writing the output file.
    /// </summary>
    private static FakeProcessRunner MkvmergeEmulator(
        Func<string, string> fixtureForFile, int remuxExitCode = 0, string remuxStdErr = "")
    {
        return new FakeProcessRunner
        {
            Handler = (_, args, _) =>
            {
                if (args.Count > 0 && args[0] == "-J")
                {
                    var json = Fixtures.Json(fixtureForFile(Path.GetFileName(args[1])));
                    return Task.FromResult(new ProcessResult(0, json, ""));
                }

                var outputPath = args[args.ToList().IndexOf("-o") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, "REMUXED");
                return Task.FromResult(new ProcessResult(remuxExitCode, "", remuxStdErr));
            },
        };
    }

    private BatchOptions Options(
        string audio = "eng", string subs = "eng",
        OverwriteBehavior overwrite = OverwriteBehavior.AutoRename) => new()
    {
        InputRoot = _inputRoot,
        OutputRoot = _outputRoot,
        MkvmergePath = "mkvmerge",
        Plan = PlanOptions.FromText(FilterMode.Languages, audio, subs),
        Overwrite = overwrite,
    };

    [Fact]
    public async Task ProcessesSkipsAndWarnsAcrossAMixedTree()
    {
        var needsRemux = AddSourceFile("movies/multilang.mkv");
        var alreadyClean = AddSourceFile("shows/s01/clean.mkv");
        var zeroMatch = AddSourceFile("german.mkv");

        var runner = MkvmergeEmulator(name => name switch
        {
            "multilang.mkv" => "movie_multilang.json",
            "clean.mkv" => "movie_eng_only.json",
            _ => "movie_ger_only.json",
        });

        var events = new List<ProgressEvent>();
        var summary = await new BatchRunner(runner).RunAsync(
            Options(), new AdaptiveScheduler(2),
            new SynchronousProgress(events), CancellationToken.None);

        Assert.Equal(3, summary.TotalFiles);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.WasCancelled);
        Assert.True(summary.BytesIn > 0);
        Assert.True(summary.BytesOut > 0);

        // Output mirrors the input subtree, and only the remuxed file exists.
        Assert.True(File.Exists(Path.Combine(_outputRoot, "movies", "multilang.mkv")));
        Assert.False(File.Exists(Path.Combine(_outputRoot, "shows", "s01", "clean.mkv")));
        Assert.False(File.Exists(Path.Combine(_outputRoot, "german.mkv")));

        // Sources are never modified.
        Assert.Equal("SOURCE-MKV-BYTES", File.ReadAllText(needsRemux));
        Assert.Equal("SOURCE-MKV-BYTES", File.ReadAllText(alreadyClean));
        Assert.Equal("SOURCE-MKV-BYTES", File.ReadAllText(zeroMatch));

        var finished = events.OfType<FileFinishedEvent>().ToList();
        Assert.Equal(3, finished.Count);
        Assert.Contains(finished, e => e.Result.Outcome == FileOutcome.Ok && e.Result.RemovedAudio == 2
            && e.Result.KeptAudio == "eng" && e.Result.KeptSubtitles == "eng");
        Assert.Contains(finished, e => e.Result.Outcome == FileOutcome.SkippedClean);
        Assert.Contains(finished, e => e.Result.Outcome == FileOutcome.SkippedNoMatch
            && e.Result.Message!.Contains("audio filter"));
    }

    [Fact]
    public async Task AutoRenameNeverClobbersAnExistingDestination()
    {
        AddSourceFile("movie.mkv");
        var existing = Path.Combine(_outputRoot, "movie.mkv");
        Directory.CreateDirectory(_outputRoot);
        File.WriteAllText(existing, "PRECIOUS");
        var runner = MkvmergeEmulator(_ => "movie_multilang.json");

        var events = new List<ProgressEvent>();
        var summary = await new BatchRunner(runner).RunAsync(
            Options(overwrite: OverwriteBehavior.AutoRename), new AdaptiveScheduler(1),
            new SynchronousProgress(events), CancellationToken.None);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal("PRECIOUS", File.ReadAllText(existing));
        var renamed = Path.Combine(_outputRoot, "movie (1).mkv");
        Assert.True(File.Exists(renamed), "output must be written under an auto-renamed name");

        var finished = Assert.Single(events.OfType<FileFinishedEvent>());
        Assert.Equal(renamed, finished.Result.OutputPath);
        var resolved = Assert.Single(events.OfType<FileOutputResolvedEvent>());
        Assert.Equal(renamed, resolved.OutputPath);
    }

    [Fact]
    public async Task OverwriteModeReplacesAnExistingDestination()
    {
        AddSourceFile("movie.mkv");
        var existing = Path.Combine(_outputRoot, "movie.mkv");
        Directory.CreateDirectory(_outputRoot);
        File.WriteAllText(existing, "OLD");
        var runner = MkvmergeEmulator(_ => "movie_multilang.json");

        var summary = await new BatchRunner(runner).RunAsync(
            Options(overwrite: OverwriteBehavior.Overwrite), new AdaptiveScheduler(1),
            null, CancellationToken.None);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal("REMUXED", File.ReadAllText(existing));
        Assert.False(File.Exists(Path.Combine(_outputRoot, "movie (1).mkv")));
    }

    [Fact]
    public async Task FilterValuesThatMatchNoFileAnywhereAreReportedInTheSummary()
    {
        AddSourceFile("multilang.mkv");
        AddSourceFile("clean.mkv");
        var runner = MkvmergeEmulator(name =>
            name == "multilang.mkv" ? "movie_multilang.json" : "movie_eng_only.json");

        var summary = await new BatchRunner(runner).RunAsync(
            Options(audio: "eng, kor", subs: "fre, zho"), new AdaptiveScheduler(2),
            null, CancellationToken.None);

        Assert.Equal(new[] { "kor" }, summary.UnmatchedAudioTokens);
        Assert.Equal(new[] { "zho" }, summary.UnmatchedSubtitleTokens);
    }

    [Fact]
    public async Task FullyMatchedOrKeepAllFiltersReportNoUnmatchedTokens()
    {
        AddSourceFile("multilang.mkv");
        var runner = MkvmergeEmulator(_ => "movie_multilang.json");

        var matched = await new BatchRunner(runner).RunAsync(
            Options(audio: "eng, ja", subs: "eng"), new AdaptiveScheduler(1),
            null, CancellationToken.None);
        Assert.Empty(matched.UnmatchedAudioTokens);
        Assert.Empty(matched.UnmatchedSubtitleTokens);

        var keepAll = await new BatchRunner(runner).RunAsync(
            Options(audio: "", subs: "none"), new AdaptiveScheduler(1),
            null, CancellationToken.None);
        Assert.Empty(keepAll.UnmatchedAudioTokens);
        Assert.Empty(keepAll.UnmatchedSubtitleTokens);
    }

    [Fact]
    public async Task MkvmergeWarningsAreReportedButTheFileCounts()
    {
        AddSourceFile("warned.mkv");
        var runner = MkvmergeEmulator(_ => "movie_multilang.json", remuxExitCode: 1,
            remuxStdErr: "Warning: cues missing\n");

        var events = new List<ProgressEvent>();
        var summary = await new BatchRunner(runner).RunAsync(
            Options(), new AdaptiveScheduler(1),
            new SynchronousProgress(events), CancellationToken.None);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        var finished = Assert.Single(events.OfType<FileFinishedEvent>());
        Assert.Equal(FileOutcome.OkWithWarnings, finished.Result.Outcome);
        Assert.Contains("cues missing", finished.Result.Message);
        Assert.True(File.Exists(Path.Combine(_outputRoot, "warned.mkv")));
    }

    [Fact]
    public async Task RemuxFailureIsCountedAndReported()
    {
        AddSourceFile("broken.mkv");
        var runner = new FakeProcessRunner
        {
            Handler = (_, args, _) => Task.FromResult(args[0] == "-J"
                ? new ProcessResult(0, Fixtures.Json("movie_multilang.json"), "")
                : new ProcessResult(2, "", "Error: something went wrong\n")),
        };

        var events = new List<ProgressEvent>();
        var summary = await new BatchRunner(runner).RunAsync(
            Options(), new AdaptiveScheduler(1),
            new SynchronousProgress(events), CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Succeeded);
        var finished = Assert.Single(events.OfType<FileFinishedEvent>());
        Assert.Equal(FileOutcome.Failed, finished.Result.Outcome);
        Assert.Contains("something went wrong", finished.Result.Message);
    }

    [Fact]
    public async Task IdentifyFailureIsCountedAsFailed()
    {
        AddSourceFile("unreadable.mkv");
        var runner = new FakeProcessRunner
        {
            Handler = (_, _, _) => Task.FromResult(new ProcessResult(2, "", "Error: not an MKV\n")),
        };

        var summary = await new BatchRunner(runner).RunAsync(
            Options(), new AdaptiveScheduler(1), null, CancellationToken.None);

        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public async Task RefusesAnOutputRootInsideTheInputRoot()
    {
        var options = new BatchOptions
        {
            InputRoot = _inputRoot,
            OutputRoot = Path.Combine(_inputRoot, "out"),
            MkvmergePath = "mkvmerge",
            Plan = PlanOptions.FromText(FilterMode.Languages, "eng", "eng"),
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => new BatchRunner(new FakeProcessRunner()).RunAsync(
                options, new AdaptiveScheduler(1), null, CancellationToken.None));
    }

    [Fact]
    public void FindsSourceFilesRecursivelyAndCaseInsensitively()
    {
        AddSourceFile("a.mkv");
        AddSourceFile("deep/nested/b.MKV");
        AddSourceFile("deep/c.mp4");
        AddSourceFile("d.M4V");
        AddSourceFile("deep/skipme.srt");
        AddSourceFile("notes.txt");

        var files = BatchRunner.FindSourceFiles(_inputRoot);

        Assert.Equal(4, files.Count);
        Assert.Contains(files, f => f.EndsWith("a.mkv"));
        Assert.Contains(files, f => f.EndsWith("b.MKV"));
        Assert.Contains(files, f => f.EndsWith("c.mp4"));
        Assert.Contains(files, f => f.EndsWith("d.M4V"));
    }

    [Fact]
    public async Task AlreadyCleanMp4IsStillRemuxedToMkvWhileACleanMkvIsSkipped()
    {
        AddSourceFile("clean.mkv");
        AddSourceFile("clean.mp4");
        var runner = MkvmergeEmulator(name =>
            name == "clean.mp4" ? "movie_mp4.json" : "movie_eng_only.json");

        // Keep-all filters: nothing to remove from either file.
        var events = new List<ProgressEvent>();
        var summary = await new BatchRunner(runner).RunAsync(
            Options(audio: "", subs: ""), new AdaptiveScheduler(1),
            new SynchronousProgress(events), CancellationToken.None);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Skipped);
        var finished = events.OfType<FileFinishedEvent>().ToList();
        Assert.Contains(finished, e => e.Result.InputPath.EndsWith("clean.mkv")
            && e.Result.Outcome == FileOutcome.SkippedClean);
        Assert.Contains(finished, e => e.Result.InputPath.EndsWith("clean.mp4")
            && e.Result.Outcome == FileOutcome.Ok
            && e.Result.RemovedAudio == 0 && e.Result.RemovedSubtitles == 0
            && e.Result.OutputPath!.EndsWith("clean.mkv"));

        // The MP4 becomes an .mkv output via a pure container conversion: no selection flags.
        var mp4Remux = Assert.Single(runner.Calls, c => c.Arguments.Contains("-o"));
        Assert.EndsWith("clean.mp4", mp4Remux.Arguments[^1]);
        Assert.DoesNotContain("-a", mp4Remux.Arguments);
        Assert.DoesNotContain("-s", mp4Remux.Arguments);
        Assert.DoesNotContain("--no-audio", mp4Remux.Arguments);
        Assert.DoesNotContain("--no-subtitles", mp4Remux.Arguments);
        Assert.Equal(new[] { "clean.mkv" },
            Directory.GetFiles(_outputRoot).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task ParallelSourcesMappingToTheSameOutputNameAreAutoRenamedNotClobbered()
    {
        // movie.mkv and movie.mp4 both map to "movie.mkv" in the output tree.
        AddSourceFile("movie.mkv");
        AddSourceFile("movie.mp4");

        // Hold both remuxes at their start until the other has also resolved its output
        // path, so File.Exists alone cannot break the tie — only the in-flight
        // reservation can. A stuck barrier fails the test instead of hanging it.
        var bothRemuxing = new TaskCompletionSource();
        int remuxes = 0;
        var runner = new FakeProcessRunner
        {
            Handler = async (_, args, _) =>
            {
                if (args[0] == "-J")
                {
                    var fixture = args[1].EndsWith(".mp4") ? "movie_mp4.json" : "movie_multilang.json";
                    return new ProcessResult(0, Fixtures.Json(fixture), "");
                }
                if (Interlocked.Increment(ref remuxes) == 2)
                    bothRemuxing.SetResult();
                await bothRemuxing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var outputPath = args[args.ToList().IndexOf("-o") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, "REMUXED");
                return new ProcessResult(0, "", "");
            },
        };

        var summary = await new BatchRunner(runner).RunAsync(
            Options(), new AdaptiveScheduler(2), null, CancellationToken.None);

        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        var outputs = Directory.GetFiles(_outputRoot).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "movie (1).mkv", "movie.mkv" }, outputs);
    }

    [Fact]
    public async Task ScanGroupsFilesByIdenticalTrackLayout()
    {
        AddSourceFile("s01/e01.mkv");
        AddSourceFile("s01/e02.mkv");
        AddSourceFile("special.mkv");

        var runner = MkvmergeEmulator(name =>
            name.StartsWith("e0") ? "movie_multilang.json" : "movie_eng_only.json");

        var report = await new BatchRunner(runner).ScanAsync(
            _inputRoot, "mkvmerge", new AdaptiveScheduler(2), null, CancellationToken.None);

        Assert.Equal(3, report.TotalFiles);
        Assert.Empty(report.Failures);
        Assert.Equal(2, report.Groups.Count);

        var bigGroup = report.Groups.Single(g => g.Files.Count == 2);
        Assert.All(bigGroup.Files, f => Assert.Contains("e0", Path.GetFileName(f)));
        Assert.Equal(6, bigGroup.Tracks.Count);
    }

    /// <summary>Reports inline (no SynchronizationContext) so tests observe every event.</summary>
    private sealed class SynchronousProgress : IProgress<ProgressEvent>
    {
        private readonly List<ProgressEvent> _events;
        public SynchronousProgress(List<ProgressEvent> events) => _events = events;

        public void Report(ProgressEvent value)
        {
            lock (_events)
                _events.Add(value);
        }
    }
}
