using MkvHippo.Core.Processes;

namespace MkvHippo.Core.Tests;

/// <summary>Scriptable stand-in for mkvmerge: answers per invocation, records every call.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string ExePath, IReadOnlyList<string> Arguments)> Calls { get; } = new();

    public Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(new ProcessResult(0, "", ""));

    public Task<ProcessResult> RunAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        lock (Calls)
            Calls.Add((exePath, arguments));
        return Handler(exePath, arguments, ct);
    }
}
