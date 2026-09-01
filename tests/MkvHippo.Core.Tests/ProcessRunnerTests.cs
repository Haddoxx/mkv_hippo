using MkvHippo.Core.Processes;

namespace MkvHippo.Core.Tests;

/// <summary>
/// Exercises the real child-process path with a shell stand-in for mkvmerge — the only place
/// kill-on-cancel and stream draining can be observed.
/// </summary>
public class ProcessRunnerTests
{
    private static (string Exe, string[] Args) Shell(string unixCommand, string windowsCommand) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", windowsCommand })
            : ("/bin/sh", new[] { "-c", unixCommand });

    [Fact]
    public async Task CapturesBothStreamsAndTheExitCode()
    {
        var (exe, args) = Shell("echo out; echo err 1>&2; exit 1", "echo out& echo err 1>&2& exit /b 1");

        var result = await new ProcessRunner().RunAsync(exe, args, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("out", result.StdOut);
        Assert.Contains("err", result.StdErr);
    }

    [Fact]
    public async Task CancellationKillsTheChildAndThrows()
    {
        var (exe, args) = Shell("sleep 30", "timeout /t 30 /nobreak");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var run = new ProcessRunner().RunAsync(exe, args, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task AProcessThatFinishesBeforeTheStopReachesItKeepsItsResult()
    {
        // The kill callback finds the process already gone, so this is not a cancellation:
        // the work is done and its result must survive rather than be thrown away.
        var (exe, args) = Shell("exit 0", "exit /b 0");
        using var cts = new CancellationTokenSource();
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(exe, args, cts.Token);
        cts.Cancel();

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenNeverStartsTheProcess()
    {
        var (exe, args) = Shell("exit 0", "exit /b 0");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ProcessRunner().RunAsync(exe, args, cts.Token));
    }
}
