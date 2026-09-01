using System.Diagnostics;

namespace MkvHippo.Core.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read both streams concurrently while waiting for exit — reading only after
        // WaitForExit deadlocks once a redirected pipe buffer fills.
        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        int killed = 0;
        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                if (process.HasExited)
                    return;
                Interlocked.Exchange(ref killed, 1);
                process.Kill(entireProcessTree: true);
            }
            catch { /* exited between the check and the kill */ }
        });

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        // Only a process we actually killed counts as cancelled. One that finished on its own
        // before the stop reached it produced a genuine result: report it rather than throwing
        // completed work away (and, upstream, deleting the file it just finished writing).
        if (Volatile.Read(ref killed) == 1)
            throw new OperationCanceledException(ct);

        return new ProcessResult(process.ExitCode, stdOut, stdErr);
    }
}
