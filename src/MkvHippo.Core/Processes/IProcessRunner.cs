namespace MkvHippo.Core.Processes;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Abstraction over child-process execution so engine logic is testable without mkvmerge.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the executable to completion, capturing stdout/stderr. Cancellation kills the
    /// entire process tree and throws <see cref="OperationCanceledException"/>. A process
    /// that exits on its own before the kill lands is not a cancellation: its result is
    /// returned normally.
    /// </summary>
    Task<ProcessResult> RunAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken ct);
}
