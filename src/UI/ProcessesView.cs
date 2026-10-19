using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace GpuMon;

/// <summary>The main view: every process using each GPU, with memory and engine load, sortable, with per-GPU summary bars on top.</summary>
public sealed class ProcessesView : UserControl
{
    sealed class Row { public ProcSample P = null!; public GpuInfo G = null!; }

    sealed class Col
    {
        public string Header; public int Width; public bool Right; public Func<Row, object> Key; public Func<Row, string> Text;
        public Col(string h, int w, bool right, Func<Row, object> key, Func<Row, string> text) { Header = h; Width = w; Right = right; Key = key; Text = text; }
    }

    static string Mb(ulong b) => (b / 1048576.0).ToString("N0");
    static string Pct(double v) => double.IsNaN(v) ? "" : v < 0.05 ? "" : v.ToString("0.#");

    static readonly Col[] Cols =
    {
        new("Process",     190, false, r => r.P.Name.ToLowerInvariant(), r => r.P.Name),
        new("PID",          60, true,  r => (double)r.P.Pid,             r => r.P.Pid.ToString()),
        new("GPU",          40, true,  r => (double)r.G.Index,           r => r.G.Index.ToString()),
        new("VRAM MB",      85, true,  r => r.P.Implausible ? -1.0 : (double)r.P.Dedicated, r => (r.P.Implausible ? "⚠ " : "") + Mb(r.P.Dedicated)),
        new("Shared MB",    80, true,  r => (double)r.P.Shared,          r => Mb(r.P.Shared)),
        new("Commit MB",    80, true,  r => (double)r.P.Committed,       r => Mb(r.P.Committed)),
        new("3D %",         52, true,  r => r.P.Eng("3D"),               r => Pct(r.P.Eng("3D"))),
        new("Compute %",    72, true,  r => r.P.Eng("Compute"),          r => Pct(r.P.Eng("Compute"))),
        new("Copy %",       58, true,  r => r.P.Eng("Copy"),             r => Pct(r.P.Eng("Copy"))),
        new("Video %",      58, true,  r => r.P.Video,                   r => Pct(r.P.Video)),
        new("MemCtl %",     66, true,  r => double.IsNaN(r.P.MemUtil) ? -1 : r.P.MemUtil, r => Pct(r.P.MemUtil)),
        new("SM %",         50, true,  r => double.IsNaN(r.P.SmUtil) ? -1 : r.P.SmUtil,   r => Pct(r.P.SmUtil)),
        new("Command line", 600, false, r => r.P.Cmd.ToLowerInvariant(), r => r.P.Cmd),
    };

    readonly GpuPanelHost<GpuSummary> _sumHost;
    readonly ListView _lv = new() { View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, Dock = DockStyle.Fill, MultiSelect = false };
    readonly Dictionary<(int, long), ListViewItem> _items = new();
    readonly TextBox _filter = new() { Width = 180, PlaceholderText = "filter name / pid / cmdline" };
    readonly CheckBox _showIdle = new() { Text = "show idle processes", AutoSize = true };
    readonly Label _foot = new() { Dock = DockStyle.Bottom, Height = 34, ForeColor = Color.FromArgb(70, 70, 70), Padding = new Padding(4, 3, 4, 0) };
    int _sortCol = 3; bool _sortDesc = true;

    public ProcessesView()
    {
        _sumHost = new GpuPanelHost<GpuSummary>(g => new GpuSummary(g));
        var top = new Panel { Dock = DockStyle.Top, Height = 142 };
        top.Controls.Add(_sumHost);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4, 3, 0, 0) };
        bar.Controls.Add(new Label { Text = "Filter:", AutoSize = true, Margin = new Padding(3, 5, 3, 0) });
        bar.Controls.Add(_filter);
        bar.Controls.Add(_showIdle);

        foreach (var c in Cols)
            _lv.Columns.Add(new ColumnHeader { Text = c.Header, Width = c.Width, TextAlign = c.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left });
        typeof(ListView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_lv, true);
        _lv.ColumnClick += (_, e) =>
        {
            if (e.Column == _sortCol) _sortDesc = !_sortDesc; else { _sortCol = e.Column; _sortDesc = e.Column >= 3 && e.Column <= 11; }
            _lv.Sort();
        };
        _lv.ListViewItemSorter = new RowSorter(this);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open file location", null, (_, _) => { if (_lv.SelectedItems.Count > 0 && ((Row)_lv.SelectedItems[0].Tag!).P.Path is { Length: > 0 } p) Process.Start("explorer.exe", $"/select,\"{p}\""); });
        menu.Items.Add("Copy command line", null, (_, _) => { if (_lv.SelectedItems.Count > 0) Clipboard.SetText(((Row)_lv.SelectedItems[0].Tag!).P.Cmd); });
        menu.Items.Add("Copy image path", null, (_, _) => { if (_lv.SelectedItems.Count > 0) Clipboard.SetText(((Row)_lv.SelectedItems[0].Tag!).P.Path); });
        _lv.ContextMenuStrip = menu;

        _foot.Text = "⚠ = Windows reports more VRAM for that process than the card physically has (typically a virtual/sparse reservation); it is excluded from totals and charts.\r\n" +
                     "VRAM/Shared/Commit come from Windows' \"GPU Process Memory\" counters; MemCtl/SM from NVML (NVIDIA only). Click a header to sort.";
        Controls.Add(_lv);
        Controls.Add(_foot);
        Controls.Add(bar);
        Controls.Add(top);
    }

    sealed class RowSorter : IComparer
    {
        readonly ProcessesView _v;
        public RowSorter(ProcessesView v) { _v = v; }
        public int Compare(object? a, object? b)
        {
            var ra = (Row)((ListViewItem)a!).Tag!; var rb = (Row)((ListViewItem)b!).Tag!;
            var col = Cols[_v._sortCol];
            int c = Comparer.Default.Compare(col.Key(ra), col.Key(rb));
            if (c == 0) c = Comparer.Default.Compare(Cols[3].Key(ra), Cols[3].Key(rb)) * -1;
            return _v._sortDesc ? -c : c;
        }
    }

    public void Update(Snapshot s, IReadOnlyList<GpuInfo> shown)
    {
        _sumHost.Show(shown);
        foreach (var g in s.Gpus)
            if (_sumHost.Panels.TryGetValue(g.Gpu.Index, out var panel)) panel.Update(g, s.Procs);

        var luids = shown.ToDictionary(g => g.Luid);
        string f = _filter.Text.Trim();
        var rows = new List<Row>();
        foreach (var p in s.Procs)
        {
            if (!luids.TryGetValue(p.Luid, out var gi)) continue;
            if (!_showIdle.Checked && p.Dedicated < 1048576 && p.Shared < 1048576 && p.Eng("3D") + p.Eng("Compute") + p.Eng("Copy") + p.Video < 0.05) continue;
            if (f.Length > 0 && !(p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Cmd.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Pid.ToString() == f)) continue;
            rows.Add(new Row { P = p, G = gi });
        }

        _lv.BeginUpdate();
        var seen = new HashSet<(int, long)>();
        foreach (var r in rows)
        {
            var key = (r.P.Pid, r.P.Luid);
            seen.Add(key);
            if (!_items.TryGetValue(key, out var it))
            {
                it = new ListViewItem(Cols[0].Text(r)) { UseItemStyleForSubItems = false, Tag = r };   // Tag must exist before Add: the sorter reads it
                for (int i = 1; i < Cols.Length; i++) it.SubItems.Add("");
                _items[key] = it;
                _lv.Items.Add(it);
            }
            it.Tag = r;
            double total = Math.Max(1, r.G.DedicatedTotal);
            for (int i = 0; i < Cols.Length; i++)
            {
                string t = Cols[i].Text(r);
                var sub = it.SubItems[i];
                if (sub.Text != t) sub.Text = t;
                Color bc = Color.Empty;
                if (i == 3) bc = r.P.Implausible ? Color.FromArgb(255, 225, 225) : Palette.Heat(r.P.Dedicated / total * 2.5, Color.FromArgb(255, 150, 60));
                else if (i == 4) bc = Palette.Heat(r.P.Shared / 1073741824.0, Color.FromArgb(235, 90, 90));
                else if (i >= 6 && i <= 9) bc = Palette.Heat((double)Cols[i].Key(r) / 100.0, Color.FromArgb(90, 160, 235));
                else if (i == 10) bc = Palette.Heat(Math.Max(0, (double)Cols[i].Key(r)) / 100.0, Color.FromArgb(180, 120, 230));
                sub.BackColor = bc.IsEmpty ? _lv.BackColor : bc;
            }
        }
        foreach (var k in _items.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _lv.Items.Remove(_items[k]);
            _items.Remove(k);
        }
        _lv.Sort();
        _lv.EndUpdate();
    }
}
