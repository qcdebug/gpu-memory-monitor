using System.Drawing.Drawing2D;
using System.Globalization;

namespace GpuMon;

/// <summary>GPU-Z style list of live sensors, each with a rolling graph and min/avg/max.</summary>
public sealed class SensorPanel : BufferedPanel
{
    readonly GpuInfo _gpu;
    readonly HistoryStore _hist;
    GpuSample? _last;
    int _scroll;
    const int RowH = 34;
    readonly HashSet<string> _engSeenActive = new();
    static readonly string[] AlwaysEngines = { "3D", "Compute", "Copy", "VideoDecode", "VideoEncode" };

    public SensorPanel(GpuInfo gpu, HistoryStore hist) { _gpu = gpu; _hist = hist; }

    public void Update(GpuSample s) { _last = s; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll = Math.Max(0, _scroll - e.Delta / 3);
        Invalidate();
    }

    IEnumerable<(string key, string label, string unit, string fmt, double fixedMax, string maxKey)> Rows()
    {
        if (_last == null) yield break;
        foreach (var d in Sensors.Defs)
            if (_last.V.ContainsKey(d.Key)) yield return (d.Key, d.Label, d.Unit, d.Fmt, d.FixedMax, d.MaxKey);
        foreach (var k in _last.V.Keys.Where(k => k.StartsWith("eng.")).OrderBy(k => k))
        {
            string t = k[4..];
            if (_last.V[k] > 0.05) _engSeenActive.Add(t);
            if (AlwaysEngines.Contains(t) || _engSeenActive.Contains(t))
                yield return (k, Sensors.EngineLabel(t), "%", "0", 100, "");
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g);
        g.Clear(BackColor);
        using var bold = new Font(Font, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.Size - 1.5f);
        using var grid = new Pen(Color.FromArgb(235, 235, 235));
        using var sep = new Pen(Color.FromArgb(220, 220, 220));
        int w = ClientSize.Width;
        // header
        using (var hb = new SolidBrush(Color.FromArgb(0x2b, 0x2d, 0x42)))
            g.FillRectangle(hb, 0, 0, w, 26);
        TextRenderer.DrawText(g, _gpu.Label, bold, new Point(8, 4), Color.White);

        int y = 28 - _scroll;
        var rows = Rows().ToList();
        int labelW = Math.Min(230, w / 3), valW = 110;
        int gx = labelW + valW + 6, gw = Math.Max(20, w - gx - 8);

        foreach (var r in rows)
        {
            if (y + RowH >= 28 && y < Height)
            {
                g.DrawLine(sep, 0, y + RowH - 1, w, y + RowH - 1);
                var ring = _hist.Sensor(_gpu.Index, r.key);
                double cur = _last!.V[r.key];
                TextRenderer.DrawText(g, r.label, Font, new Rectangle(8, y, labelW, RowH), Color.FromArgb(40, 40, 40),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
                string txt = cur.ToString(r.fmt, CultureInfo.CurrentCulture) + (r.unit.Length > 0 ? " " + r.unit : "");
                TextRenderer.DrawText(g, txt, bold, new Rectangle(labelW, y, valW, RowH), Color.Black,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                DrawGraph(g, ring, r, new Rectangle(gx, y + 3, gw, RowH - 7), small);
            }
            y += RowH;
        }
        foreach (var (key, label) in Sensors.TextDefs)
        {
            if (_last == null || !_last.T.TryGetValue(key, out var val)) continue;
            if (y + RowH >= 28 && y < Height)
            {
                g.DrawLine(sep, 0, y + RowH - 1, w, y + RowH - 1);
                TextRenderer.DrawText(g, label, Font, new Rectangle(8, y, labelW, RowH), Color.FromArgb(40, 40, 40),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                bool warn = key == "throttle" && !val.StartsWith("None");
                TextRenderer.DrawText(g, val, bold, new Rectangle(labelW, y, w - labelW - 8, RowH), warn ? Color.Firebrick : Color.Black,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
            y += RowH;
        }
        int total = y + _scroll;
        int maxScroll = Math.Max(0, total - Height);
        if (_scroll > maxScroll) { _scroll = maxScroll; }
    }

    void DrawGraph(Graphics g, Ring ring, (string key, string label, string unit, string fmt, double fixedMax, string maxKey) r, Rectangle rc, Font small)
    {
        using (var bg = new SolidBrush(Color.FromArgb(248, 249, 251))) g.FillRectangle(bg, rc);
        using (var bd = new Pen(Color.FromArgb(215, 218, 224))) g.DrawRectangle(bd, rc);
        int n = ring.Count;
        if (n < 2) return;

        double max = r.fixedMax;
        if (max <= 0 && r.maxKey.Length > 0 && _last!.V.TryGetValue(r.maxKey, out var mk) && mk > 0) max = mk;
        double mn = double.MaxValue, mx = 0, sum = 0; int cnt = 0;
        for (int i = 0; i < n; i++)
        {
            float v = ring[i]; if (float.IsNaN(v)) continue;
            mn = Math.Min(mn, v); mx = Math.Max(mx, v); sum += v; cnt++;
        }
        if (cnt == 0) return;
        if (max <= 0) max = Math.Max(mx * 1.15, 1);

        int vis = Math.Min(n, rc.Width);                 // 1 px per sample, newest at right
        var pts = new List<PointF>(vis);
        for (int i = n - vis; i < n; i++)
        {
            float v = ring[i];
            float x = rc.Right - 1 - (n - 1 - i);
            if (float.IsNaN(v)) { Flush(g, pts, rc); pts.Clear(); continue; }
            float y = rc.Bottom - 1 - (float)(Math.Clamp(v / max, 0, 1) * (rc.Height - 2));
            pts.Add(new PointF(x, y));
        }
        Flush(g, pts, rc);

        string stat = $"min {mn.ToString(r.fmt)}  avg {(sum / cnt).ToString(r.fmt)}  max {mx.ToString(r.fmt)}";
        TextRenderer.DrawText(g, stat, small, new Rectangle(rc.X + 3, rc.Y, rc.Width - 6, rc.Height), Color.FromArgb(110, 110, 120),
            TextFormatFlags.Top | TextFormatFlags.Right);
    }

    static void Flush(Graphics g, List<PointF> pts, Rectangle rc)
    {
        if (pts.Count == 0) return;
        if (pts.Count == 1) { g.FillRectangle(Brushes.SeaGreen, pts[0].X, pts[0].Y, 1, 1); return; }
        var poly = new List<PointF>(pts) { new(pts[^1].X, rc.Bottom - 1), new(pts[0].X, rc.Bottom - 1) };
        using (var fill = new SolidBrush(Color.FromArgb(60, 46, 139, 87))) g.FillPolygon(fill, poly.ToArray());
        using var line = new Pen(Color.FromArgb(46, 139, 87), 1.4f);
        g.DrawLines(line, pts.ToArray());
    }
}
