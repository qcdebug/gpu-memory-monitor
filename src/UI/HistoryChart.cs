using System.Drawing.Drawing2D;

namespace GpuMon;

/// <summary>Stacked area chart of dedicated VRAM by process name over time, plus a strip for shared (spilled) system memory.</summary>
public sealed class HistoryChart : BufferedPanel
{
    readonly GpuInfo _gpu;
    readonly HistoryStore _hist;
    int _hover = -1;   // tick index under the mouse, -1 = none
    const int LegendW = 250;
    const int TopN = 9;

    public HistoryChart(GpuInfo gpu, HistoryStore hist) { _gpu = gpu; _hist = hist; }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); _hover = HitTick(e.X); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    Rectangle PlotRect => new(52, 34, Math.Max(10, Width - LegendW - 64), Math.Max(10, (int)((Height - 34 - 70) * 0.74)));
    Rectangle SharedRect { get { var p = PlotRect; return new(p.X, p.Bottom + 30, p.Width, Math.Max(10, Height - p.Bottom - 30 - 28)); } }

    int HitTick(int x)
    {
        if (!_hist.Mem.TryGetValue(_gpu.Index, out var l) || l.Count < 2) return -1;
        var p = PlotRect;
        if (x < p.X || x > p.Right) return -1;
        return Math.Clamp((int)Math.Round((x - p.X) / (double)p.Width * (l.Count - 1)), 0, l.Count - 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g);
        g.Clear(BackColor);
        using var bold = new Font(Font, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.Size - 1f);
        TextRenderer.DrawText(g, $"{_gpu.Label}  —  dedicated VRAM by process", bold, new Point(8, 6), Color.Black);

        if (!_hist.Mem.TryGetValue(_gpu.Index, out var ticks) || ticks.Count < 2)
        {
            TextRenderer.DrawText(g, "collecting…", Font, new Point(60, 50), Color.Gray);
            return;
        }
        var plot = PlotRect;
        int n = ticks.Count;
        double total = Math.Max(1, ticks.Max(t => t.Total));

        // choose the series: biggest peaks get their own colour, the rest fold into "other"
        var peak = new Dictionary<string, float>();
        foreach (var t in ticks) for (int i = 0; i < t.Names.Length; i++) peak[t.Names[i]] = Math.Max(peak.GetValueOrDefault(t.Names[i]), t.MB[i]);
        var series = peak.OrderByDescending(k => k.Value).Take(TopN).Select(k => k.Key).ToList();

        // stack: bottom = unattributed (driver/OS), then the series, then other
        int layers = series.Count + 2;
        var cum = new float[layers][];
        for (int l = 0; l < layers; l++) cum[l] = new float[n];
        for (int x = 0; x < n; x++)
        {
            var t = ticks[x];
            float acc = t.Unattributed;
            cum[0][x] = acc;
            float rest = 0;
            for (int i = 0; i < t.Names.Length; i++) if (!series.Contains(t.Names[i])) rest += t.MB[i];
            for (int s = 0; s < series.Count; s++)
            {
                int idx = Array.IndexOf(t.Names, series[s]);
                acc += idx >= 0 ? t.MB[idx] : 0;
                cum[s + 1][x] = acc;
            }
            cum[layers - 1][x] = acc + rest;
        }

        // grid + axis
        using var gridPen = new Pen(Color.FromArgb(228, 228, 232));
        for (int q = 0; q <= 4; q++)
        {
            int yy = plot.Bottom - plot.Height * q / 4;
            g.DrawLine(gridPen, plot.X, yy, plot.Right, yy);
            TextRenderer.DrawText(g, $"{total * q / 4 / 1024:0.#} GB", small, new Rectangle(0, yy - 8, plot.X - 4, 16), Color.Gray, TextFormatFlags.Right);
        }
        PointF Pt(int x, float v) => new(plot.X + (float)(x * (plot.Width / (double)(n - 1))), plot.Bottom - (float)(Math.Min(v, total) / total * plot.Height));

        var colors = new Color[layers];
        colors[0] = Palette.Unattributed;
        for (int s = 0; s < series.Count; s++) colors[s + 1] = Palette.For(series[s]);
        colors[layers - 1] = Palette.Other;
        for (int l = layers - 1; l >= 0; l--)
        {
            var poly = new List<PointF>(n * 2);
            for (int x = 0; x < n; x++) poly.Add(Pt(x, cum[l][x]));
            for (int x = n - 1; x >= 0; x--) poly.Add(Pt(x, l == 0 ? 0 : cum[l - 1][x]));
            using var br = new SolidBrush(colors[l]);
            g.FillPolygon(br, poly.ToArray());
        }
        using (var bd = new Pen(Color.FromArgb(150, 150, 160))) g.DrawRectangle(bd, plot);

        // shared memory strip
        var sr = SharedRect;
        double smax = Math.Max(256, ticks.Max(t => t.Shared) * 1.2);
        TextRenderer.DrawText(g, "Shared (system) memory used by this GPU — growth here means VRAM is spilling over PCIe", small,
            new Point(plot.X, plot.Bottom + 10), Color.FromArgb(120, 40, 40));
        using (var bg = new SolidBrush(Color.FromArgb(250, 244, 244))) g.FillRectangle(bg, sr);
        var sp = new List<PointF>();
        for (int x = 0; x < n; x++)
            sp.Add(new PointF(sr.X + (float)(x * (sr.Width / (double)(n - 1))), sr.Bottom - (float)(ticks[x].Shared / smax * sr.Height)));
        using (var line = new Pen(Color.Firebrick, 1.6f)) g.DrawLines(line, sp.ToArray());
        using (var bd = new Pen(Color.FromArgb(150, 150, 160))) g.DrawRectangle(bd, sr);
        TextRenderer.DrawText(g, $"{smax:N0} MB", small, new Rectangle(0, sr.Y - 2, sr.X - 4, 16), Color.Gray, TextFormatFlags.Right);

        // time axis
        var span = ticks[^1].T - ticks[0].T;
        TextRenderer.DrawText(g, $"← {span.TotalMinutes:0.#} min ago", small, new Point(plot.X, sr.Bottom + 4), Color.Gray);
        TextRenderer.DrawText(g, "now", small, new Rectangle(plot.Right - 60, sr.Bottom + 4, 60, 16), Color.Gray, TextFormatFlags.Right);

        // legend (values at the hovered tick, or now)
        int hi = _hover >= 0 ? _hover : n - 1;
        var ht = ticks[hi];
        int lx = plot.Right + 14, ly = plot.Y;
        TextRenderer.DrawText(g, _hover >= 0 ? ht.T.ToString("HH:mm:ss") : "now", bold, new Point(lx, ly), Color.Black);
        ly += 22;
        float usedNow = ht.Unattributed + ht.MB.Sum();
        TextRenderer.DrawText(g, $"Used {usedNow:N0} / {ht.Total:N0} MB   shared {ht.Shared:N0} MB", small, new Point(lx, ly), Color.FromArgb(60, 60, 60));
        ly += 20;
        void Legend(Color c, string name, float mb)
        {
            using var b = new SolidBrush(c);
            g.FillRectangle(b, lx, ly + 3, 11, 11);
            TextRenderer.DrawText(g, name, Font, new Rectangle(lx + 16, ly, LegendW - 90, 18), Color.Black, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, $"{mb:N0} MB", Font, new Rectangle(lx + LegendW - 76, ly, 66, 18), Color.Black, TextFormatFlags.Right);
            ly += 19;
        }
        for (int s = series.Count - 1; s >= 0; s--)
        {
            int idx = Array.IndexOf(ht.Names, series[s]);
            Legend(colors[s + 1], series[s], idx >= 0 ? ht.MB[idx] : 0);
        }
        float rest2 = 0;
        for (int i = 0; i < ht.Names.Length; i++) if (!series.Contains(ht.Names[i])) rest2 += ht.MB[i];
        Legend(Palette.Other, "other processes", rest2);
        Legend(Palette.Unattributed, "driver / OS / unattributed", ht.Unattributed);

        if (_hover >= 0)
        {
            using var hp = new Pen(Color.FromArgb(200, 0, 0, 0)) { DashStyle = DashStyle.Dot };
            float hx = plot.X + (float)(_hover * (plot.Width / (double)(n - 1)));
            g.DrawLine(hp, hx, plot.Y, hx, sr.Bottom);
        }
    }
}
