using System.Drawing.Imaging;
using System.Security.Principal;

namespace GpuMon;

public sealed class MainForm : Form
{
    readonly Sampler _sampler;
    readonly HistoryStore _hist = new();
    readonly Recorder _rec = new();

    readonly ToolStripComboBox _gpuBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly ToolStripComboBox _rateBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    readonly ToolStripButton _pauseBtn = new("Pause") { CheckOnClick = true };
    readonly ToolStripButton _recBtn = new("Record to CSV") { CheckOnClick = true };
    readonly ToolStripButton _topBtn = new("Always on top") { CheckOnClick = true };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly ProcessesView _procs = new() { Dock = DockStyle.Fill };
    readonly GpuPanelHost<SensorPanel> _sensors;
    readonly GpuPanelHost<HistoryChart> _charts;
    readonly GpuPanelHost<ListView> _info;

    Snapshot? _latest;
    int _pendingFlag;
    readonly string[] _args;
    string? _shotDir;

    public MainForm(string[] args)
    {
        _args = args;
        Text = $"GpuMon {CrashLog.Version} — GPU memory by process";
        Size = new Size(1380, 860);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont!;
        Icon = SystemIcons.Application;

        _sampler = new Sampler();
        CrashLog.Context = () => "NVML: " + _sampler.NvmlStatus + Environment.NewLine + "Windows GPU counters ok: " + _sampler.CountersOk + Environment.NewLine + "GPUs: " + string.Join(" | ", _sampler.Gpus.Select(g => $"{g.Label} [{g.Vendor}, bus {g.Bus}, {g.DedicatedTotal / 1048576} MB]"));
        _sensors = new GpuPanelHost<SensorPanel>(g => new SensorPanel(g, _hist));
        _charts = new GpuPanelHost<HistoryChart>(g => new HistoryChart(g, _hist));
        _info = new GpuPanelHost<ListView>(g =>
        {
            var lv = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, HeaderStyle = ColumnHeaderStyle.None };
            lv.Columns.Add("", 210); lv.Columns.Add("", 330);
            foreach (var (k, v) in g.Details) lv.Items.Add(new ListViewItem(new[] { k, v }));
            return lv;
        });

        // toolbar
        var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
        _gpuBox.Items.Add("All GPUs");
        foreach (var g in _sampler.Gpus) _gpuBox.Items.Add(g.Label);
        _gpuBox.SelectedIndex = 0;
        _gpuBox.SelectedIndexChanged += (_, _) => { ApplySelection(); if (_latest != null) Render(_latest); };
        foreach (var r in new[] { "0.5 s", "1 s", "2 s", "5 s" }) _rateBox.Items.Add(r);
        _rateBox.SelectedIndex = 1;
        _rateBox.SelectedIndexChanged += (_, _) =>
        {
            _sampler.IntervalMs = _rateBox.SelectedIndex switch { 0 => 500, 1 => 1000, 2 => 2000, _ => 5000 };
            _sampler.WakeNow();
        };
        _pauseBtn.CheckedChanged += (_, _) => _sampler.Paused = _pauseBtn.Checked;
        _recBtn.CheckedChanged += (_, _) =>
        {
            if (_recBtn.Checked) { _rec.Start(); _status.Text = "Recording to " + _rec.Folder; }
            else _rec.Stop();
        };
        _topBtn.CheckedChanged += (_, _) => TopMost = _topBtn.Checked;
        tb.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripLabel("Show:"), _gpuBox, new ToolStripSeparator(),
            new ToolStripLabel("Refresh:"), _rateBox, new ToolStripSeparator(),
            _pauseBtn, _recBtn, _topBtn,
        });

        // tabs
        AddTab("Processes", _procs);
        AddTab("Sensors", _sensors);
        AddTab("VRAM history", _charts);
        AddTab("Displays", new DisplaysView(() => _sampler.Gpus));
        AddTab("GPU info", _info);

        var ss = new StatusStrip();
        ss.Items.Add(_status);

        Controls.Add(_tabs);
        Controls.Add(ss);
        Controls.Add(tb);

        ApplySelection();
        _sampler.Updated += OnSnapshot;

        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        _status.Text = $"{_sampler.NvmlStatus}  |  Windows GPU counters: {(_sampler.CountersOk ? "ok" : "UNAVAILABLE")}  |  {(admin ? "elevated" : "not elevated (names/paths of some system processes may be hidden)")}";

        int si = Array.IndexOf(_args, "--shot");
        if (si >= 0 && si + 1 < _args.Length) _shotDir = _args[si + 1];

        Shown += (_, _) => { _sampler.Start(); if (_shotDir != null) RunShots(); };
        FormClosing += (_, _) => { _sampler.Dispose(); _rec.Dispose(); };
    }

    void AddTab(string title, Control c)
    {
        var tp = new TabPage(title) { Padding = Padding.Empty };
        c.Dock = DockStyle.Fill;
        tp.Controls.Add(c);
        _tabs.TabPages.Add(tp);
    }

    IReadOnlyList<GpuInfo> Selected()
    {
        int i = _gpuBox.SelectedIndex;
        return i <= 0 ? _sampler.Gpus : new[] { _sampler.Gpus[i - 1] };
    }

    void ApplySelection()
    {
        var sel = Selected();
        _sensors.Show(sel); _charts.Show(sel); _info.Show(sel);
    }

    void OnSnapshot(Snapshot s)
    {
        _latest = s;
        if (Interlocked.Exchange(ref _pendingFlag, 1) == 0)
        {
            try { BeginInvoke(() => { Interlocked.Exchange(ref _pendingFlag, 0); if (_latest != null) Render(_latest); }); }
            catch (InvalidOperationException) { }   // form closing
        }
    }

    void Render(Snapshot s)
    {
        _hist.Add(s);   // history keeps every tick even when a tab is hidden
        if (_rec.Active) _rec.Write(s);
        var sel = Selected();
        _procs.Update(s, sel);
        foreach (var g in s.Gpus)
        {
            if (_sensors.Panels.TryGetValue(g.Gpu.Index, out var sp)) sp.Update(g);
            if (_charts.Panels.TryGetValue(g.Gpu.Index, out var cp)) cp.Invalidate();
        }
        string spill = string.Join("  ", s.Gpus.Select(g => $"GPU{g.Gpu.Index}: {g.V.GetValueOrDefault("mem.pct"):0}% VRAM"));
        if (!_recBtn.Checked)
            _status.Text = $"{spill}   |   sampled {s.Time:HH:mm:ss} in {s.CollectMs:0} ms   |   {_sampler.NvmlStatus}";
        else
            _status.Text = $"RECORDING → {_rec.Folder}   ({_rec.Rows} sensor rows)   |   {spill}";
    }

    // ---- developer aid: render every tab to PNG (used for verification) -------------------------
    async void RunShots()
    {
        Activate(); BringToFront();
        await Task.Delay(6000);
        Directory.CreateDirectory(_shotDir!);
        for (int i = 0; i < _tabs.TabPages.Count; i++)
        {
            _tabs.SelectedIndex = i;
            await Task.Delay(1500);
            using var bmp = new Bitmap(Width, Height);
            using (var gr = Graphics.FromImage(bmp)) gr.CopyFromScreen(Location, Point.Empty, Size);
            bmp.Save(Path.Combine(_shotDir!, $"tab{i}_{_tabs.TabPages[i].Text.Replace(' ', '_')}.png"), ImageFormat.Png);
        }
        Close();
    }
}
