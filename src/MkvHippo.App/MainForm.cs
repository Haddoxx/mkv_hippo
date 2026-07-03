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

    public MainForm()
    {
        InitializeComponent();
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
        };

        var progress = new Progress<ProgressEvent>(evt => HandleProgress(evt, inputRoot));
        SetBusy(true);
        Log($"starting: {inputRoot} → {outputRoot}");
        try
        {
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
        Log($"scanning: {inputRoot}");
        try
        {
            var report = await Task.Run(() =>
                new BatchRunner().ScanAsync(inputRoot, mkvmergePath, _scheduler!, progress, _cts!.Token));
            LogScanReport(report, inputRoot);
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

            case FileFinishedEvent finished:
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
        switch (result.Outcome)
        {
            case FileOutcome.Ok:
                Log($"[ok] {name} — removed a:{result.RemovedAudio} s:{result.RemovedSubtitles}");
                break;
            case FileOutcome.OkWithWarnings:
                Log($"[ok] {name} — removed a:{result.RemovedAudio} s:{result.RemovedSubtitles}");
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
