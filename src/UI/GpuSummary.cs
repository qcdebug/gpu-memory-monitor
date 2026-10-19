namespace GpuMon;

/// <summary>Per-GPU header: a VRAM bar segmented by process, a shared-memory bar, and the key bus/memory indicators.</summary>
public sealed class GpuSummary : BufferedPanel
{
    readonly GpuInfo _gpu;
    GpuSample? _s;
    List<ProcSample> _procs = new();

    public GpuSummary(GpuInfo gpu) { _gpu = gpu; Height = 136; MinimumSize = new Size(300, 136); }

    public void Update(GpuSample s, IEnumerable<ProcSample> procs)
    {
        _s = s;
        _procs = procs.Where(p => p.Luid == _gpu.Luid && !p.Implausible).ToList();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Paint2(e); } catch (Exception ex) { e.Graphics.DrawString(ex.ToString(), Font, Brushes.Red, 4, 4); }
    }

    void Paint2(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g);
        g.Clear(Color.FromArgb(246, 247, 250));
        using var bold = new Font(Font, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.Size - 1f);
        using (var bd = new Pen(Color.FromArgb(205, 208, 215))) g.DrawRectangle(bd, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, _gpu.Label, bold, new Point(8, 5), Color.Black);
        if (_s == null) return;

        double totalMb = Math.Max(1, _s.V.GetValueOrDefault("mem.total", _gpu.DedicatedTotal / 1048576.0));
        double usedMb = _s.V.GetValueOrDefault("mem.used");
        double pct = usedMb / totalMb * 100;
        TextRenderer.DrawText(g, $"VRAM {usedMb / 1024:0.0} / {totalMb / 1024:0.0} GB  ({pct:0}%)", bold,
            new Rectangle(0, 5, Width - 10, 18), pct > 90 ? Color.Firebrick : Color.Black, TextFormatFlags.Right);

        var bar = new Rectangle(8, 27, Width - 16, 20);
        using (var bg = new SolidBrush(Color.FromArgb(225, 226, 232))) g.FillRectangle(bg, bar);
        // segments
        var segs = new List<(Color c, double mb, string name)>();
        var byName = _procs.GroupBy(p => p.Name).Select(x => (name: x.Key, mb: x.Sum(p => (double)p.Dedicated) / 1048576.0))
                           .Where(x => x.mb >= 1).OrderByDescending(x => x.mb).ToList();
        double attributed = 0;
        foreach (var x in byName.Take(6)) { segs.Add((Palette.For(x.name), x.mb, x.name)); attributed += x.mb; }
        double rest = byName.Skip(6).Sum(x => x.mb);
        if (rest > 0) { segs.Add((Palette.Other, rest, "other")); attributed += rest; }
        double unattr = Math.Max(0, usedMb - attributed);
        segs.Add((Palette.Unattributed, unattr, "driver/OS"));
        float x0 = bar.X;
        foreach (var sg in segs)
        {
            float w = (float)(sg.mb / totalMb * bar.Width);
            if (w < 0.5f) continue;
            using var b = new SolidBrush(sg.c);
            g.FillRectangle(b, x0, bar.Y, Math.Min(w, bar.Right - x0), bar.Height);
            x0 += w;
        }
        using (var bd = new Pen(Color.FromArgb(140, 140, 150))) g.DrawRectangle(bd, bar);

        // legend row
        int lx = 8, ly = 50;
        foreach (var sg in segs.Where(s => s.mb >= 1))
        {
            string t = $"{sg.name} {sg.mb / 1024:0.0}G";
            var sz = TextRenderer.MeasureText(g, t, small);
            if (lx + sz.Width + 18 > Width - 6) { lx = 8; ly += 16; if (ly > 82) break; }
            using var b = new SolidBrush(sg.c);
            g.FillRectangle(b, lx, ly + 3, 9, 9);
            TextRenderer.DrawText(g, t, small, new Point(lx + 12, ly), Color.FromArgb(50, 50, 50), TextFormatFlags.NoPadding);
            lx += sz.Width + 22;
        }

        // indicators
        double mc = _s.Get("load.mem"), load = _s.Get("load.gpu");
        double shared = _s.V.GetValueOrDefault("mem.shared");
        string line = $"GPU {Fmt(load, "0", "%")}    Mem-ctrl {Fmt(mc, "0", "%")}    Shared {shared:N0} MB";
        TextRenderer.DrawText(g, line, small, new Rectangle(8, Height - 40, Width - 16, 18), Color.FromArgb(40, 40, 40), TextFormatFlags.EndEllipsis);

        // PCIe line: current link vs what the card supports, and traffic vs link capacity
        if (_s.V.TryGetValue("pcie.gen", out var gen))
        {
            double w = _s.V["pcie.width"], mg = _s.V["pcie.maxgen"], mw = _s.V["pcie.maxwidth"];
            double rx = _s.Get("pcie.rx"), tx = _s.Get("pcie.tx"), cap = _s.V["pcie.cap"];
            bool busy = Math.Max(rx, tx) > 1000 || load > 30;
            bool narrow = w < mw, slow = gen < mg;
            Color col = narrow || (slow && busy) ? Color.FromArgb(200, 110, 0) : Color.FromArgb(40, 40, 40);
            string note = narrow ? $"   ⚠ card supports x{mw:0}" : slow ? (busy ? $"   ⚠ card supports Gen{mg:0}" : "   (idle downshift)") : "";
            string txt = $"PCIe Gen{gen:0} x{w:0}{note}   ↓ {Fmt(rx, "N0", "")}  ↑ {Fmt(tx, "N0", "")}  of {cap / 1000:0.#} GB/s per direction";
            using var bf = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(g, txt, bf, new Rectangle(8, Height - 21, Width - 16, 18), col, TextFormatFlags.EndEllipsis);
        }
    }

    static string Fmt(double v, string f, string unit) => double.IsNaN(v) ? "n/a" : v.ToString(f) + unit;
}
