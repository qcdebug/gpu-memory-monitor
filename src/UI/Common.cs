using System.Drawing.Drawing2D;

namespace GpuMon;

public sealed class Ring
{
    readonly float[] _a;
    int _n, _head;
    public Ring(int cap) { _a = new float[cap]; }
    public int Count => _n;
    public int Capacity => _a.Length;
    public void Add(float v) { _a[_head] = v; _head = (_head + 1) % _a.Length; if (_n < _a.Length) _n++; }
    /// <summary>0 = oldest sample.</summary>
    public float this[int i] => _a[(_head - _n + i + _a.Length * 2) % _a.Length];
}

public sealed class MemTick
{
    public DateTime T;
    public string[] Names = Array.Empty<string>();
    public float[] MB = Array.Empty<float>();
    public float Unattributed, Shared, Total;
}

/// <summary>Rolling history shared by the graph views. Touched only from the UI thread.</summary>
public sealed class HistoryStore
{
    public const int Cap = 900;
    public readonly Dictionary<(int gpu, string key), Ring> Sensors = new();
    public readonly Dictionary<int, List<MemTick>> Mem = new();

    public Ring Sensor(int gpu, string key)
    {
        if (!Sensors.TryGetValue((gpu, key), out var r)) Sensors[(gpu, key)] = r = new Ring(Cap);
        return r;
    }

    public void Add(Snapshot s)
    {
        foreach (var g in s.Gpus)
        {
            int gi = g.Gpu.Index;
            foreach (var kv in g.V) Sensor(gi, kv.Key).Add((float)kv.Value);
            // keys that vanished this tick still need a gap so rings stay time-aligned
            foreach (var kv in Sensors.Where(k => k.Key.gpu == gi && !g.V.ContainsKey(k.Key.key)).ToList()) kv.Value.Add(float.NaN);

            var byName = new Dictionary<string, float>();
            double sum = 0;
            foreach (var p in s.Procs)
            {
                if (p.Luid != g.Gpu.Luid || p.Implausible) continue;
                float mb = p.Dedicated / 1048576f;
                byName[p.Name] = byName.GetValueOrDefault(p.Name) + mb;
                sum += mb;
            }
            var top = byName.Where(k => k.Value >= 1f).OrderByDescending(k => k.Value).ToArray();
            if (!Mem.TryGetValue(gi, out var list)) Mem[gi] = list = new List<MemTick>();
            double used = g.V.GetValueOrDefault("mem.used");
            list.Add(new MemTick
            {
                T = s.Time,
                Names = top.Select(k => k.Key).ToArray(),
                MB = top.Select(k => k.Value).ToArray(),
                Unattributed = (float)Math.Max(0, used - sum),
                Shared = (float)g.V.GetValueOrDefault("mem.shared"),
                Total = (float)g.V.GetValueOrDefault("mem.total"),
            });
            if (list.Count > Cap) list.RemoveRange(0, list.Count - Cap);
        }
    }
}

public static class Palette
{
    static readonly Color[] Cols =
    {
        Color.FromArgb(0x1f,0x77,0xb4), Color.FromArgb(0xff,0x7f,0x0e), Color.FromArgb(0x2c,0xa0,0x2c), Color.FromArgb(0xd6,0x27,0x28),
        Color.FromArgb(0x94,0x67,0xbd), Color.FromArgb(0x8c,0x56,0x4b), Color.FromArgb(0xe3,0x77,0xc2), Color.FromArgb(0x17,0xbe,0xcf),
        Color.FromArgb(0xbc,0xbd,0x22), Color.FromArgb(0x39,0x3b,0x79), Color.FromArgb(0xe7,0xba,0x52), Color.FromArgb(0x63,0x93,0x9a),
    };
    public static readonly Color Other = Color.FromArgb(0xa6, 0xa6, 0xa6);
    public static readonly Color Unattributed = Color.FromArgb(0x5b, 0x5b, 0x66);

    static readonly Dictionary<string, int> _assigned = new();

    /// <summary>Colour per process name: assigned in order of first appearance so distinct apps get distinct colours (UI thread only).</summary>
    public static Color For(string name)
    {
        string k = name.ToLowerInvariant();
        if (!_assigned.TryGetValue(k, out int i)) _assigned[k] = i = _assigned.Count;
        return Cols[i % Cols.Length];
    }

    public static Color Heat(double frac, Color hot)
    {
        frac = Math.Clamp(frac, 0, 1);
        return Color.FromArgb(255 - (int)((255 - hot.R) * frac), 255 - (int)((255 - hot.G) * frac), 255 - (int)((255 - hot.B) * frac));
    }
}

/// <summary>Hosts one child control per selected GPU, side by side.</summary>
public sealed class GpuPanelHost<T> : TableLayoutPanel where T : Control
{
    readonly Func<GpuInfo, T> _factory;
    public readonly Dictionary<int, T> Panels = new();
    public GpuPanelHost(Func<GpuInfo, T> factory)
    {
        _factory = factory;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
    }

    public void Show(IEnumerable<GpuInfo> gpus)
    {
        var list = gpus.ToList();
        if (list.Select(g => g.Index).SequenceEqual(Panels.Keys) && Controls.Count == list.Count) return;
        SuspendLayout();
        Controls.Clear(); Panels.Clear();
        ColumnStyles.Clear(); RowStyles.Clear();
        ColumnCount = Math.Max(1, list.Count); RowCount = 1;
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < list.Count; i++)
        {
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / list.Count));
            var c = _factory(list[i]);
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(2);
            Panels[list[i].Index] = c;
            Controls.Add(c, i, 0);
        }
        ResumeLayout();
    }
}

public abstract class BufferedPanel : Control
{
    protected BufferedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
        Font = SystemFonts.MessageBoxFont!;
    }
    protected static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; }
}
