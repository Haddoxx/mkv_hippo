namespace MkvHippo.App;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    private Label lblInput = null!;
    private TextBox txtInput = null!;
    private Button btnBrowseInput = null!;
    private Label lblOutput = null!;
    private TextBox txtOutput = null!;
    private Button btnBrowseOutput = null!;
    private GroupBox grpMode = null!;
    private RadioButton rbLanguages = null!;
    private RadioButton rbTrackIds = null!;
    private GroupBox grpParallel = null!;
    private RadioButton rbParallel1 = null!;
    private RadioButton rbParallel2 = null!;
    private RadioButton rbParallel3 = null!;
    private RadioButton rbParallel4 = null!;
    private Label lblAudio = null!;
    private TextBox txtAudioFilter = null!;
    private Label lblSubtitles = null!;
    private TextBox txtSubtitleFilter = null!;
    private Label lblHint = null!;
    private Button btnScan = null!;
    private Button btnStart = null!;
    private Button btnStop = null!;
    private ProgressBar progressBar = null!;
    private Label lblCounter = null!;
    private RichTextBox txtLog = null!;
    private StatusStrip statusStrip = null!;
    private ToolStripStatusLabel lblStatus = null!;
    private ToolStripStatusLabel lblGaugeCpu = null!;
    private ToolStripStatusLabel lblGaugeDisk = null!;
    private ToolStripStatusLabel lblGaugeNet = null!;
    private ToolStripStatusLabel lblGaugeRate = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null)
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        lblInput = new Label();
        txtInput = new TextBox();
        btnBrowseInput = new Button();
        lblOutput = new Label();
        txtOutput = new TextBox();
        btnBrowseOutput = new Button();
        grpMode = new GroupBox();
        rbLanguages = new RadioButton();
        rbTrackIds = new RadioButton();
        grpParallel = new GroupBox();
        rbParallel1 = new RadioButton();
        rbParallel2 = new RadioButton();
        rbParallel3 = new RadioButton();
        rbParallel4 = new RadioButton();
        lblAudio = new Label();
        txtAudioFilter = new TextBox();
        lblSubtitles = new Label();
        txtSubtitleFilter = new TextBox();
        lblHint = new Label();
        btnScan = new Button();
        btnStart = new Button();
        btnStop = new Button();
        progressBar = new ProgressBar();
        lblCounter = new Label();
        txtLog = new RichTextBox();
        statusStrip = new StatusStrip();
        lblStatus = new ToolStripStatusLabel();
        lblGaugeCpu = new ToolStripStatusLabel();
        lblGaugeDisk = new ToolStripStatusLabel();
        lblGaugeNet = new ToolStripStatusLabel();
        lblGaugeRate = new ToolStripStatusLabel();
        grpMode.SuspendLayout();
        grpParallel.SuspendLayout();
        statusStrip.SuspendLayout();
        SuspendLayout();
        //
        // lblInput
        //
        lblInput.AutoSize = true;
        lblInput.Location = new Point(12, 15);
        lblInput.Name = "lblInput";
        lblInput.Text = "Input folder:";
        //
        // txtInput
        //
        txtInput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtInput.Location = new Point(110, 12);
        txtInput.Name = "txtInput";
        txtInput.Size = new Size(650, 23);
        //
        // btnBrowseInput
        //
        btnBrowseInput.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowseInput.Location = new Point(770, 11);
        btnBrowseInput.Name = "btnBrowseInput";
        btnBrowseInput.Size = new Size(100, 25);
        btnBrowseInput.Text = "Browse…";
        btnBrowseInput.UseVisualStyleBackColor = true;
        btnBrowseInput.Click += OnBrowseInput;
        //
        // lblOutput
        //
        lblOutput.AutoSize = true;
        lblOutput.Location = new Point(12, 44);
        lblOutput.Name = "lblOutput";
        lblOutput.Text = "Output folder:";
        //
        // txtOutput
        //
        txtOutput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtOutput.Location = new Point(110, 41);
        txtOutput.Name = "txtOutput";
        txtOutput.Size = new Size(650, 23);
        txtOutput.TextChanged += OnOutputTextChanged;
        //
        // btnBrowseOutput
        //
        btnBrowseOutput.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowseOutput.Location = new Point(770, 40);
        btnBrowseOutput.Name = "btnBrowseOutput";
        btnBrowseOutput.Size = new Size(100, 25);
        btnBrowseOutput.Text = "Browse…";
        btnBrowseOutput.UseVisualStyleBackColor = true;
        btnBrowseOutput.Click += OnBrowseOutput;
        //
        // grpMode
        //
        grpMode.Controls.Add(rbLanguages);
        grpMode.Controls.Add(rbTrackIds);
        grpMode.Location = new Point(12, 75);
        grpMode.Name = "grpMode";
        grpMode.Size = new Size(220, 52);
        grpMode.TabStop = false;
        grpMode.Text = "Filter by";
        //
        // rbLanguages
        //
        rbLanguages.AutoSize = true;
        rbLanguages.Checked = true;
        rbLanguages.Location = new Point(12, 22);
        rbLanguages.Name = "rbLanguages";
        rbLanguages.TabStop = true;
        rbLanguages.Text = "Languages";
        rbLanguages.UseVisualStyleBackColor = true;
        //
        // rbTrackIds
        //
        rbTrackIds.AutoSize = true;
        rbTrackIds.Location = new Point(115, 22);
        rbTrackIds.Name = "rbTrackIds";
        rbTrackIds.Text = "Track IDs";
        rbTrackIds.UseVisualStyleBackColor = true;
        //
        // grpParallel
        //
        grpParallel.Controls.Add(rbParallel1);
        grpParallel.Controls.Add(rbParallel2);
        grpParallel.Controls.Add(rbParallel3);
        grpParallel.Controls.Add(rbParallel4);
        grpParallel.Location = new Point(250, 75);
        grpParallel.Name = "grpParallel";
        grpParallel.Size = new Size(220, 52);
        grpParallel.TabStop = false;
        grpParallel.Text = "Parallel files";
        //
        // rbParallel1
        //
        rbParallel1.AutoSize = true;
        rbParallel1.Location = new Point(15, 22);
        rbParallel1.Name = "rbParallel1";
        rbParallel1.Tag = "1";
        rbParallel1.Text = "1";
        rbParallel1.UseVisualStyleBackColor = true;
        rbParallel1.CheckedChanged += OnParallelChanged;
        //
        // rbParallel2
        //
        rbParallel2.AutoSize = true;
        rbParallel2.Checked = true;
        rbParallel2.Location = new Point(65, 22);
        rbParallel2.Name = "rbParallel2";
        rbParallel2.TabStop = true;
        rbParallel2.Tag = "2";
        rbParallel2.Text = "2";
        rbParallel2.UseVisualStyleBackColor = true;
        rbParallel2.CheckedChanged += OnParallelChanged;
        //
        // rbParallel3
        //
        rbParallel3.AutoSize = true;
        rbParallel3.Location = new Point(115, 22);
        rbParallel3.Name = "rbParallel3";
        rbParallel3.Tag = "3";
        rbParallel3.Text = "3";
        rbParallel3.UseVisualStyleBackColor = true;
        rbParallel3.CheckedChanged += OnParallelChanged;
        //
        // rbParallel4
        //
        rbParallel4.AutoSize = true;
        rbParallel4.Location = new Point(165, 22);
        rbParallel4.Name = "rbParallel4";
        rbParallel4.Tag = "4";
        rbParallel4.Text = "4";
        rbParallel4.UseVisualStyleBackColor = true;
        rbParallel4.CheckedChanged += OnParallelChanged;
        //
        // lblAudio
        //
        lblAudio.AutoSize = true;
        lblAudio.Location = new Point(12, 143);
        lblAudio.Name = "lblAudio";
        lblAudio.Text = "Audio filter:";
        //
        // txtAudioFilter
        //
        txtAudioFilter.Location = new Point(110, 140);
        txtAudioFilter.Name = "txtAudioFilter";
        txtAudioFilter.PlaceholderText = "empty = keep all";
        txtAudioFilter.Size = new Size(250, 23);
        //
        // lblSubtitles
        //
        lblSubtitles.AutoSize = true;
        lblSubtitles.Location = new Point(380, 143);
        lblSubtitles.Name = "lblSubtitles";
        lblSubtitles.Text = "Subtitle filter:";
        //
        // txtSubtitleFilter
        //
        txtSubtitleFilter.Location = new Point(480, 140);
        txtSubtitleFilter.Name = "txtSubtitleFilter";
        txtSubtitleFilter.PlaceholderText = "empty = keep all";
        txtSubtitleFilter.Size = new Size(250, 23);
        //
        // lblHint
        //
        lblHint.AutoSize = true;
        lblHint.ForeColor = SystemColors.GrayText;
        lblHint.Location = new Point(12, 170);
        lblHint.Name = "lblHint";
        lblHint.Text = "Comma-separated values to keep (e.g. eng, jpn). \"none\" drops all tracks of that type; empty keeps all. \"und\" matches tracks without a language.";
        //
        // btnScan
        //
        btnScan.Location = new Point(12, 196);
        btnScan.Name = "btnScan";
        btnScan.Size = new Size(100, 28);
        btnScan.Text = "Scan";
        btnScan.UseVisualStyleBackColor = true;
        btnScan.Click += OnScan;
        //
        // btnStart
        //
        btnStart.Location = new Point(120, 196);
        btnStart.Name = "btnStart";
        btnStart.Size = new Size(100, 28);
        btnStart.Text = "Start";
        btnStart.UseVisualStyleBackColor = true;
        btnStart.Click += OnStart;
        //
        // btnStop
        //
        btnStop.Enabled = false;
        btnStop.Location = new Point(228, 196);
        btnStop.Name = "btnStop";
        btnStop.Size = new Size(100, 28);
        btnStop.Text = "Stop";
        btnStop.UseVisualStyleBackColor = true;
        btnStop.Click += OnStop;
        //
        // progressBar
        //
        progressBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        progressBar.Location = new Point(340, 198);
        progressBar.Name = "progressBar";
        progressBar.Size = new Size(420, 24);
        //
        // lblCounter
        //
        lblCounter.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        lblCounter.Location = new Point(766, 202);
        lblCounter.Name = "lblCounter";
        lblCounter.Size = new Size(104, 15);
        lblCounter.Text = "0/0";
        lblCounter.TextAlign = ContentAlignment.MiddleRight;
        //
        // txtLog
        //
        txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        txtLog.BackColor = SystemColors.Window;
        txtLog.DetectUrls = false;
        txtLog.Font = new Font("Consolas", 9F);
        txtLog.HideSelection = false;
        txtLog.Location = new Point(12, 234);
        txtLog.Name = "txtLog";
        txtLog.ReadOnly = true;
        txtLog.Size = new Size(858, 336);
        txtLog.Text = "";
        txtLog.WordWrap = false;
        //
        // statusStrip
        //
        statusStrip.Items.AddRange(new ToolStripItem[] { lblStatus, lblGaugeCpu, lblGaugeDisk, lblGaugeNet, lblGaugeRate });
        statusStrip.Location = new Point(0, 579);
        statusStrip.Name = "statusStrip";
        statusStrip.ShowItemToolTips = true;
        statusStrip.Size = new Size(884, 22);
        //
        // lblStatus
        //
        lblStatus.Name = "lblStatus";
        lblStatus.Size = new Size(28, 17);
        lblStatus.Spring = true;
        lblStatus.Text = "idle";
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        //
        // gauge labels (frame drawn around the current bottleneck via BorderSides)
        //
        lblGaugeCpu.BorderStyle = Border3DStyle.SunkenOuter;
        lblGaugeCpu.Name = "lblGaugeCpu";
        lblGaugeCpu.Text = "CPU –";
        lblGaugeCpu.ToolTipText = "System-wide CPU utilization (Task Manager's metric)";
        lblGaugeDisk.BorderStyle = Border3DStyle.SunkenOuter;
        lblGaugeDisk.Name = "lblGaugeDisk";
        lblGaugeDisk.Text = "DISK –";
        lblGaugeDisk.ToolTipText = "Physical disk active time (all disks)";
        lblGaugeNet.BorderStyle = Border3DStyle.SunkenOuter;
        lblGaugeNet.Name = "lblGaugeNet";
        lblGaugeNet.Text = "NET –";
        lblGaugeNet.ToolTipText = "Busiest network adapter, % of its link speed";
        lblGaugeRate.Name = "lblGaugeRate";
        lblGaugeRate.Text = "– MB/s";
        lblGaugeRate.ToolTipText = "Remux write throughput";
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(884, 601);
        Controls.Add(lblInput);
        Controls.Add(txtInput);
        Controls.Add(btnBrowseInput);
        Controls.Add(lblOutput);
        Controls.Add(txtOutput);
        Controls.Add(btnBrowseOutput);
        Controls.Add(grpMode);
        Controls.Add(grpParallel);
        Controls.Add(lblAudio);
        Controls.Add(txtAudioFilter);
        Controls.Add(lblSubtitles);
        Controls.Add(txtSubtitleFilter);
        Controls.Add(lblHint);
        Controls.Add(btnScan);
        Controls.Add(btnStart);
        Controls.Add(btnStop);
        Controls.Add(progressBar);
        Controls.Add(lblCounter);
        Controls.Add(txtLog);
        Controls.Add(statusStrip);
        MinimumSize = new Size(760, 480);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "MKV Hippo";
        grpMode.ResumeLayout(false);
        grpMode.PerformLayout();
        grpParallel.ResumeLayout(false);
        grpParallel.PerformLayout();
        statusStrip.ResumeLayout(false);
        statusStrip.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }
}
