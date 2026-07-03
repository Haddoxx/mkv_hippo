using MkvHippo.Core;
using MkvHippo.Core.Mkv;
using MkvHippo.Core.Reporting;
using MkvHippo.Core.Scheduling;

namespace MkvHippo.App;

public partial class MainForm : Form
{
    private AdaptiveScheduler? _scheduler;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private int _parallelTarget = 2;
    private bool _outputWasAutoFilled;
    private bool _settingOutputProgrammatically;
    private string? _lastScannedInput;

    private readonly ResourceMonitor _monitor = new();
    private readonly System.Diagnostics.Stopwatch _sessionClock = new();
    private readonly Dictionary<string, string> _inFlightOutputs = new();
    private Font? _gaugeBoldFont;
    private BottleneckStats? _sessionStats;
    private ThroughputMeter? _throughput;
    private long _completedOutputBytes;

    public MainForm()
    {
        InitializeComponent();

        var gaugeTimer = new System.Windows.Forms.Timer(components) { Interval = 500 };
        gaugeTimer.Tick += OnGaugeTick;
        gaugeTimer.Start();
        FormClosed += (_, _) =>
        {
            _monitor.Dispose();
            _gaugeBoldFont?.Dispose();
        };
    }

    // --- Folder pickers ---

    private void OnBrowseInput(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder to scan for .mkv files" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        txtInput.Text = dialog.SelectedPath;
        if (string.IsNullOrWhiteSpace(txtOutput.Text) || _outputWasAutoFilled)
        {
            _settingOutputProgrammatically = true;
            txtOutput.Text = OutputPathMapper.DefaultOutputRoot(dialog.SelectedPath);
            _settingOutputProgrammatically = false;
            _outputWasAutoFilled = true;
        }
    }

    private void OnBrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder to write processed files into" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            txtOutput.Text = dialog.SelectedPath;
    }

    private void OnOutputTextChanged(object? sender, EventArgs e)
    {
        if (!_settingOutputProgrammatically)
            _outputWasAutoFilled = false;
    }

    // --- Parallelism: enabled at all times, applies to a running batch immediately ---

    private void OnParallelChanged(object? sender, EventArgs e)
    {
        if (sender is RadioButton { Checked: true, Tag: string tag } && int.TryParse(tag, out int n))
        {
            _parallelTarget = n;
            _scheduler?.SetTarget(n);
        }
    }

    // --- Actions ---

    private async void OnStart(object? sender, EventArgs e)
    {
        var setup = ValidateSetup(requireOutput: true);
        if (setup is null)
            return;
        var (inputRoot, outputRoot, mkvmergePath) = setup.Value;

        var plan = PlanOptions.FromText(
            rbTrackIds.Checked ? FilterMode.TrackIds : FilterMode.Languages,
            txtAudioFilter.Text, txtSubtitleFilter.Text);
        var options = new BatchOptions
        {
            InputRoot = inputRoot,
            OutputRoot = outputRoot!,
            MkvmergePath = mkvmergePath,
            Plan = plan,
            Overwrite = rbOverwrite.Checked ? OverwriteBehavior.Overwrite : OverwriteBehavior.AutoRename,
        };

        var progress = new Progress<ProgressEvent>(evt => HandleProgress(evt, inputRoot));
        SetBusy(true);
        BeginResourceSession();
        try
        {
            if (!HasScanned(inputRoot))
            {
                Log("input folder not scanned yet — scanning first");
                var report = await ExecuteScanAsync(inputRoot, mkvmergePath, progress);
                if (report.WasCancelled)
                    return;
                Log("");
            }

            Log($"starting: {inputRoot} → {outputRoot}");
            var summary = await Task.Run(() =>
                new BatchRunner().RunAsync(options, _scheduler!, progress, _cts!.Token));
            LogSummary(summary);
        }
        catch (Exception ex)
        {
            Log($"[fail] {ex.Message}");
        }
        finally
        {
            EndResourceSession();
            SetBusy(false);
        }
    }

    private async void OnScan(object? sender, EventArgs e)
    {
        var setup = ValidateSetup(requireOutput: false);
        if (setup is null)
            return;
        var (inputRoot, _, mkvmergePath) = setup.Value;

        var progress = new Progress<ProgressEvent>(evt => HandleProgress(evt, inputRoot));
        SetBusy(true);
        try
        {
            await ExecuteScanAsync(inputRoot, mkvmergePath, progress);
        }
        catch (Exception ex)
        {
            Log($"[fail] {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Runs the identify-only scan, logs the report, and remembers the scanned input.</summary>
    private async Task<ScanReport> ExecuteScanAsync(
        string inputRoot, string mkvmergePath, IProgress<ProgressEvent> progress)
    {
        Log($"scanning: {inputRoot}");
        var report = await Task.Run(() =>
            new BatchRunner().ScanAsync(inputRoot, mkvmergePath, _scheduler!, progress, _cts!.Token));
        LogScanReport(report, inputRoot);
        if (!report.WasCancelled)
            _lastScannedInput = Path.GetFullPath(inputRoot);
        return report;
    }

    private bool HasScanned(string inputRoot) =>
        _lastScannedInput is not null && string.Equals(
            _lastScannedInput, Path.GetFullPath(inputRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // --- Resource gauges & bottleneck tracking ---

    private void BeginResourceSession()
    {
        _sessionStats = new BottleneckStats();
        _throughput = new ThroughputMeter();
        _completedOutputBytes = 0;
        _inFlightOutputs.Clear();
        _sessionClock.Restart();
    }

    private void EndResourceSession()
    {
        if (_sessionStats is { SampleCount: > 0 })
            Log(_sessionStats.Summarize());
        _sessionStats = null;
        _throughput = null;
        _inFlightOutputs.Clear();
        _sessionClock.Stop();
    }

    private void OnGaugeTick(object? sender, EventArgs e)
    {
        var (cpu, disk, net) = _monitor.Sample();

        lblGaugeCpu.Text = $"CPU {FormatPercent(cpu)}";
        lblGaugeDisk.Text = $"DISK {FormatPercent(disk)}";
        lblGaugeNet.Text = $"NET {FormatPercent(net)}";

        var gauges = new (ToolStripStatusLabel Label, double? Value)[]
        {
            (lblGaugeCpu, cpu), (lblGaugeDisk, disk), (lblGaugeNet, net),
        };
        var top = gauges.Where(g => g.Value.HasValue).OrderByDescending(g => g.Value).FirstOrDefault();
        _gaugeBoldFont ??= new Font(statusStrip.Font, FontStyle.Bold);
        foreach (var (label, _) in gauges)
        {
            bool isBottleneck = ReferenceEquals(label, top.Label);
            label.BorderSides = isBottleneck
                ? ToolStripStatusLabelBorderSides.All
                : ToolStripStatusLabelBorderSides.None;
            label.Font = isBottleneck ? _gaugeBoldFont : statusStrip.Font;
        }

        _sessionStats?.AddSample(cpu, disk, net);

        if (_throughput is not null)
        {
            long total = _completedOutputBytes;
            foreach (var outputPath in _inFlightOutputs.Values)
            {
                try
                {
                    var info = new FileInfo(outputPath);
                    if (info.Exists)
                        total += info.Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            double rate = _throughput.Update(total, _sessionClock.Elapsed.TotalSeconds);
            lblGaugeRate.Text = $"{rate / (1024.0 * 1024.0):0.0} MB/s";
        }
        else
        {
            lblGaugeRate.Text = "– MB/s";
        }
    }

    private static string FormatPercent(double? value) => value is double v ? $"{v:0}%" : "–";

    private void OnStop(object? sender, EventArgs e)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            Log("[warn] stop requested — pending files dropped, running mkvmerge processes killed");
            _cts.Cancel();
        }
    }

    /// <summary>Validates the form and prepares the run; null means a message was already shown.</summary>
    private (string InputRoot, string? OutputRoot, string MkvmergePath)? ValidateSetup(bool requireOutput)
    {
        var inputRoot = txtInput.Text.Trim();
        if (inputRoot.Length == 0 || !Directory.Exists(inputRoot))
        {
            MessageBox.Show(this, "Choose an existing input folder first.", "MKV Hippo",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        string? outputRoot = null;
        if (requireOutput)
        {
            outputRoot = txtOutput.Text.Trim();
            if (outputRoot.Length == 0)
                outputRoot = OutputPathMapper.DefaultOutputRoot(inputRoot);
            try
            {
                OutputPathMapper.EnsureValidRoots(inputRoot, outputRoot);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(this, ex.Message, "MKV Hippo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }

        var mkvmergePath = MkvmergeLocator.Locate();
        if (mkvmergePath is null)
        {
            Log($"[fail] {MkvmergeLocator.InstallHint}");
            MessageBox.Show(this, MkvmergeLocator.InstallHint, "MKV Hippo",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }

        _cts = new CancellationTokenSource();
        _scheduler = new AdaptiveScheduler(_parallelTarget, _cts.Token);
        _scheduler.StateChanged += OnSchedulerStateChanged;
        return (inputRoot, outputRoot, mkvmergePath);
    }

    // --- Progress plumbing (all on the UI thread via IProgress) ---

    private void HandleProgress(ProgressEvent evt, string inputRoot)
    {
        switch (evt)
        {
            case BatchStartedEvent started:
                progressBar.Minimum = 0;
                progressBar.Maximum = Math.Max(started.TotalFiles, 1);
                progressBar.Value = 0;
                lblCounter.Text = $"0/{started.TotalFiles}";
                Log($"found {started.TotalFiles} file(s)");
                break;

            case FileOutputResolvedEvent resolved:
                _inFlightOutputs[resolved.InputPath] = resolved.OutputPath;
                break;

            case FileFinishedEvent finished:
                _inFlightOutputs.Remove(finished.Result.InputPath);
                if (finished.Result.Succeeded)
                    _completedOutputBytes += finished.Result.BytesOut;
                LogFileResult(finished.Result, inputRoot);
                progressBar.Value = Math.Min(finished.Processed, progressBar.Maximum);
                lblCounter.Text = $"{finished.Processed}/{finished.TotalFiles}";
                break;

            case ScanFileEvent scanned:
                if (!scanned.Identified)
                    Log($"[fail] {Relative(inputRoot, scanned.InputPath)} — {scanned.Error}");
                progressBar.Value = Math.Min(scanned.Processed, progressBar.Maximum);
                lblCounter.Text = $"{scanned.Processed}/{scanned.TotalFiles}";
                break;
        }
    }

    private void LogFileResult(FileResult result, string inputRoot)
    {
        var name = Relative(inputRoot, result.InputPath);
        // "kept s:all" is the tell-tale of an accidentally empty filter.
        var kept = result.KeptAudio is null ? ""
            : $", kept a:{result.KeptAudio} s:{result.KeptSubtitles}";
        // Auto-rename kicked in: the destination file name differs from the source's.
        if (result.OutputPath is not null
            && !string.Equals(Path.GetFileName(result.OutputPath), Path.GetFileName(result.InputPath),
                StringComparison.OrdinalIgnoreCase))
        {
            kept += $", renamed → {Path.GetFileName(result.OutputPath)}";
        }
        switch (result.Outcome)
        {
            case FileOutcome.Ok:
                Log($"[ok] {name} — removed a:{result.RemovedAudio} s:{result.RemovedSubtitles}{kept}");
                break;
            case FileOutcome.OkWithWarnings:
                Log($"[ok] {name} — removed a:{result.RemovedAudio} s:{result.RemovedSubtitles}{kept}");
                Log($"[warn] {name} — {result.Message}");
                break;
            case FileOutcome.SkippedClean:
                Log($"[skip] {name} — already clean");
                break;
            case FileOutcome.SkippedNoMatch:
                Log($"[warn] {name} — skipped: {result.Message}");
                break;
            case FileOutcome.Cancelled:
                Log($"[warn] {name} — cancelled");
                break;
            default:
                Log($"[fail] {name} — {result.Message}");
                break;
        }
    }

    private void LogSummary(BatchSummary summary)
    {
        Log("");
        var stopped = summary.WasCancelled ? " (stopped)" : "";
        var cancelled = summary.Cancelled > 0 ? $", {summary.Cancelled} cancelled" : "";
        Log($"done{stopped}: {summary.Succeeded} processed, {summary.Skipped} skipped, " +
            $"{summary.Failed} failed{cancelled} of {summary.TotalFiles}");
        Log($"bytes: {FormatBytes(summary.BytesIn)} in → {FormatBytes(summary.BytesOut)} out; " +
            $"elapsed {summary.Elapsed:hh\\:mm\\:ss}");

        // Suppressed after a stop: unscanned files could make the warning a false alarm.
        if (!summary.WasCancelled)
        {
            if (summary.UnmatchedAudioTokens.Count > 0)
                Log($"[warn] audio filter value(s) \"{string.Join(", ", summary.UnmatchedAudioTokens)}\" " +
                    "matched no tracks in any file");
            if (summary.UnmatchedSubtitleTokens.Count > 0)
                Log($"[warn] subtitle filter value(s) \"{string.Join(", ", summary.UnmatchedSubtitleTokens)}\" " +
                    "matched no tracks in any file");
        }
    }

    private void LogScanReport(ScanReport report, string inputRoot)
    {
        Log("");
        Log($"scan complete: {report.TotalFiles} file(s), {report.Groups.Count} distinct track layout(s)" +
            (report.WasCancelled ? " (stopped)" : ""));
        foreach (var group in report.Groups)
        {
            Log($"layout shared by {group.Files.Count} file(s):");
            foreach (var track in group.Tracks)
                Log($"    #{track.Id} {track.Type.ToString().ToLowerInvariant()} [{track.DisplayLanguage}] {track.Codec}");
            foreach (var file in group.Files)
                Log($"      {Relative(inputRoot, file)}");
        }
        foreach (var failure in report.Failures)
            Log($"[fail] {Relative(inputRoot, failure.InputPath)} — {failure.Message}");
    }

    // --- Scheduler status ---

    private void OnSchedulerStateChanged()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(UpdateSchedulerStatus);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void UpdateSchedulerStatus()
    {
        var scheduler = _scheduler;
        if (!_busy || scheduler is null)
        {
            lblStatus.Text = "idle";
            return;
        }

        int active = scheduler.ActiveCount;
        int target = scheduler.Target;
        lblStatus.Text = active > target
            ? $"scaling down: {active} active → target {target}"
            : $"running: {active} active / target {target}, {scheduler.PendingCount} queued";
    }

    // --- Helpers ---

    private void SetBusy(bool busy)
    {
        _busy = busy;
        btnStart.Enabled = !busy;
        btnScan.Enabled = !busy;
        btnStop.Enabled = busy;
        txtInput.Enabled = !busy;
        txtOutput.Enabled = !busy;
        btnBrowseInput.Enabled = !busy;
        btnBrowseOutput.Enabled = !busy;
        grpMode.Enabled = !busy;
        grpDestination.Enabled = !busy;
        txtAudioFilter.Enabled = !busy;
        txtSubtitleFilter.Enabled = !busy;
        // grpParallel deliberately stays enabled: the target is adjustable mid-batch.

        if (!busy)
        {
            if (_scheduler is not null)
                _scheduler.StateChanged -= OnSchedulerStateChanged;
            _scheduler = null;
            _cts?.Dispose();
            _cts = null;
            lblStatus.Text = "idle";
        }
    }

    private void Log(string line)
    {
        txtLog.AppendText(line + Environment.NewLine);
    }

    private static string Relative(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
