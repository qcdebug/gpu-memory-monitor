namespace GpuMon;

/// <summary>Desktop layout: one rectangle per active display, scaled, coloured by the GPU that drives it.</summary>
public sealed class ArrangementMap : BufferedPanel
{
    List<DisplayDesc> _d = new();
    Func<long, GpuInfo?> _gpu = _ => null;
    Dictionary<string, double> _hz = new();
    static readonly Color[] GpuCols = { Color.FromArgb(0x3b, 0x82, 0xc4), Color.FromArgb(0x2e, 0x9e, 0x6b), Color.FromArgb(0xc4, 0x7a, 0x2e), Color.FromArgb(0x8a, 0x5b, 0xc4) };
    public static Color GpuColor(int i) => GpuCols[Math.Abs(i) % GpuCols.Length];

    public void Set(List<DisplayDesc> d, Func<long, GpuInfo?> gpu, Dictionary<string, double> hz) { _d = d; _gpu = gpu; _hz = hz; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g);
        g.Clear(Color.FromArgb(240, 242, 246));
        using var bold = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(g, "Monitor arrangement (to scale)", bold, new Point(8, 5), Color.Black);
        if (_d.Count == 0) { TextRenderer.DrawText(g, "no active displays found", Font, new Point(12, 34), Color.Gray); return; }

        int minX = _d.Min(x => x.X), minY = _d.Min(x => x.Y), maxX = _d.Max(x => x.X + x.W), maxY = _d.Max(x => x.Y + x.H);
        var area = new Rectangle(12, 28, Width - 24, Height - 38);
        double sc = Math.Min(area.Width / (double)Math.Max(1, maxX - minX), area.Height / (double)Math.Max(1, maxY - minY));
        double ox = area.X + (area.Width - (maxX - minX) * sc) / 2, oy = area.Y + (area.Height - (maxY - minY) * sc) / 2;

        foreach (var d in _d)
        {
            var r = new RectangleF((float)(ox + (d.X - minX) * sc), (float)(oy + (d.Y - minY) * sc), (float)(d.W * sc), (float)(d.H * sc));
            var gpu = _gpu(d.AdapterLuid);
            using var fill = new SolidBrush(Color.FromArgb(200, GpuColor(gpu?.Index ?? 0)));
            g.FillRectangle(fill, r);
            using var pen = new Pen(d.Primary ? Color.Black : Color.FromArgb(60, 60, 60), d.Primary ? 3f : 1.5f);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            _hz.TryGetValue(d.Key, out var mhz);
            string t = $"{d.Number}  {d.MonitorName}{(d.Primary ? "  (main)" : "")}\n{d.W}×{d.H} @ {d.RefreshHz:0.#} Hz" +
                       (double.IsNaN(mhz) || mhz == 0 ? "" : $"  (measured {mhz:0.#})") + $"\nGPU {gpu?.Index.ToString() ?? "?"}";
            TextRenderer.DrawText(g, t, Font, Rectangle.Round(r), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }
}

/// <summary>Everything known about one display, with a rolling graph of its measured refresh rate.</summary>
public sealed class DisplayCard : BufferedPanel
{
    public DisplayDesc D;
    public GpuInfo? Gpu;
    public readonly Ring Hz = new(120);
    public double LastHz = double.NaN;
    public bool HasWarning => _rows.Any(x => x.label == "⚠");
    List<(string? label, string value, Color color)> _rows = new();
    const int RowH = 18, GraphH = 58;

    public DisplayCard(DisplayDesc d) { D = d; Width = 580; Margin = new Padding(6); Rebuild(); }

    public void Push(double hz) { LastHz = hz; Hz.Add((float)hz); Rebuild(); Invalidate(); }
    public void Set(DisplayDesc d, GpuInfo? gpu) { D = d; Gpu = gpu; Rebuild(); Invalidate(); }

    static readonly Color Warn = Color.FromArgb(200, 90, 0), Normal = Color.FromArgb(30, 30, 30), Head = Color.FromArgb(40, 70, 130), Good = Color.FromArgb(30, 130, 60);

    void Rebuild()
    {
        var r = new List<(string?, string, Color)>();
        void Row(string l, string v, Color? c = null) => r.Add((l, v, c ?? Normal));
        void Heading(string t) => r.Add((null, t, Head));
        var d = D;

        // ---- diagnosis -------------------------------------------------------------------------
        double measured = LastHz;
        if (d.MaxHzAtRes > 0 && d.RefreshHz < 31 && d.MaxHzAtRes > 31)
            Row("⚠", $"Running at {d.RefreshHz:0.#} Hz although Windows offers up to {d.MaxHzAtRes} Hz at {d.W}×{d.H}. The faster mode is available but not selected.", Warn);
        else if (d.RefreshHz < 31 && d.W * d.H >= 3840 * 2000)
            Row("⚠", $"Only ≤30 Hz is offered at {d.W}×{d.H}. On DisplayPort this means the link or EDID negotiation topped out (DP version setting in the monitor menu, cable, adapter/KVM, or a failed link training).", Warn);
        if (!double.IsNaN(measured) && measured > 1 && Math.Abs(measured - d.RefreshHz) > Math.Max(2, d.RefreshHz * 0.05))
            Row("⚠", $"Measured vertical-blank rate ({measured:0.#} Hz) differs from the Windows mode ({d.RefreshHz:0.#} Hz).", Warn);
        if (d.Edid is { HasRange: true } ed0 && d.RefreshHz > ed0.VMax + 0.5)
            Row("⚠", $"Mode refresh is above the monitor's advertised vertical range ({ed0.VMin}–{ed0.VMax} Hz).", Warn);
        if (d.Edid is { HasRange: true, MaxClockMHz: > 0 } ed1 && d.PixelClockMHz > ed1.MaxClockMHz)
            Row("⚠", $"Pixel clock {d.PixelClockMHz:0} MHz exceeds the monitor's advertised maximum ({ed1.MaxClockMHz} MHz).", Warn);

        Heading("Connection");
        Row("GPU", Gpu != null ? $"GPU {Gpu.Index}: {Gpu.Name}" : "unknown");
        Row("Output", d.Tech + $"   (Windows output {d.GdiName})");
        Row("Desktop", $"{d.W}×{d.H} at ({d.X}, {d.Y}){(d.Primary ? ", main display" : "")}, {d.Rotation}, scaling {d.Scaling}");

        Heading("Signal timing");
        Row("Refresh (mode)", $"{d.RefreshHz:0.###} Hz   ({d.ScanOrder})");
        Row("Refresh (measured)", double.IsNaN(measured) ? "measuring…" : $"{measured:0.##} Hz   vertical blanks counted on this output");
        Row("Pixel clock", $"{d.PixelClockMHz:0.##} MHz");
        Row("Sync frequencies", $"horizontal {d.HSyncKHz:0.###} kHz,  vertical {d.VSyncHz:0.###} Hz");
        if (d.TotalW > 0)
            Row("Raster", $"active {d.ActiveW}×{d.ActiveH}, total {d.TotalW}×{d.TotalH}  (blanking {d.TotalW - d.ActiveW} × {d.TotalH - d.ActiveH})");
        Row("Colour", $"{d.Bpc} bits/channel, {d.EncodingName}, HDR {(d.HdrEnabled ? "on" : d.HdrCapable ? "off (supported)" : "not supported")}" +
                       (d.SdrWhiteNits > 0 ? $", SDR white {d.SdrWhiteNits:0} nits" : ""));

        Heading("Link bandwidth");
        var fits = DisplayTopology.Links.Where(l => l.Gbps >= d.NeedGbps).Select(l => l.Name).ToList();
        var no = DisplayTopology.Links.Where(l => l.Gbps < d.NeedGbps).Select(l => l.Name).ToList();
        Row("Mode needs", $"≈ {d.NeedGbps:0.0} Gbps  (pixel clock incl. blanking × bits/pixel)");
        Row("Fits in", fits.Count > 0 ? string.Join(", ", fits) : "none listed", Good);
        if (no.Count > 0) Row("Too much for", string.Join(", ", no), Warn);
        Row("Note", "Estimate: Windows hides the actual DP link rate/lanes.");

        Heading("Modes Windows offers at this resolution");
        Row($"{d.W}×{d.H}", d.HzAtRes.Count > 0 ? string.Join(", ", d.HzAtRes) + " Hz" : "none reported",
            d.MaxHzAtRes > 0 && d.MaxHzAtRes <= 31 ? Warn : Normal);

        if (d.Edid is { } e)
        {
            Heading("Monitor (EDID)");
            Row("Model", $"{(e.Name.Length > 0 ? e.Name : d.MonitorName)}  [{e.Mfg}{e.ProductCode:X4}]" + (e.Serial.Length > 0 ? $"   serial {e.Serial}" : ""));
            Row("Made", $"week {e.Week}, {e.Year}      size {e.WidthCm}×{e.HeightCm} cm" + (e.WidthCm > 0 ? $"  ({Math.Sqrt(e.WidthCm * e.WidthCm + e.HeightCm * e.HeightCm) / 2.54:0.#}\")" : ""));
            if (e.NativeW > 0) Row("Preferred timing", $"{e.NativeW}×{e.NativeH} @ {e.NativeHz:0.##} Hz   (pixel clock {e.NativeClockMHz:0.##} MHz)");
            if (e.HasRange) Row("Advertised range", $"vertical {e.VMin}–{e.VMax} Hz,  horizontal {e.HMin}–{e.HMax} kHz,  max pixel clock {e.MaxClockMHz} MHz");
            Row("Extensions", $"{e.Extensions} block(s)" + (e.HdmiVsdb ? $", HDMI 1.x data (max TMDS {e.HdmiMaxTmdsMHz} MHz)" : "") + (e.HdmiForum ? $", HDMI 2.x data (max {e.HfMaxTmdsMHz} MHz)" : ""));
        }
        else { Heading("Monitor (EDID)"); Row("EDID", "not readable from the registry"); }

        _rows = r;
        int h = 34 + _rows.Count * RowH + 8 + GraphH + 12;
        // warnings can wrap onto 2 lines; budget for that
        h += _rows.Count(x => x.label == "⚠") * RowH;
        if (Height != h) Height = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g);
        g.Clear(Color.White);
        using var bold = new Font(Font, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.Size - 1.5f);
        var gc = ArrangementMap.GpuColor(Gpu?.Index ?? 0);
        using (var hb = new SolidBrush(Color.FromArgb(0x2b, 0x2d, 0x42))) g.FillRectangle(hb, 0, 0, Width, 28);
        using (var gb = new SolidBrush(gc)) g.FillRectangle(gb, 0, 0, 6, 28);
        TextRenderer.DrawText(g, $"Display {D.Number} — {D.MonitorName}{(D.Primary ? "   (main)" : "")}", bold, new Point(14, 5), Color.White);
        using (var bd = new Pen(Color.FromArgb(205, 208, 215))) g.DrawRectangle(bd, 0, 0, Width - 1, Height - 1);

        int y = 34, labW = 150;
        foreach (var (label, value, color) in _rows)
        {
            if (label == null)
            {
                y += 4;
                TextRenderer.DrawText(g, value, bold, new Point(10, y), color);
                using var ln = new Pen(Color.FromArgb(225, 228, 235)); g.DrawLine(ln, 10, y + RowH - 1, Width - 10, y + RowH - 1);
                y += RowH; continue;
            }
            if (label == "⚠")
            {
                var rc = new Rectangle(10, y, Width - 20, RowH * 2);
                using var wb = new SolidBrush(Color.FromArgb(255, 244, 225)); g.FillRectangle(wb, rc);
                TextRenderer.DrawText(g, "⚠ " + value, Font, Rectangle.Inflate(rc, -4, -1), color, TextFormatFlags.WordBreak | TextFormatFlags.Left);
                y += RowH * 2 + 1; continue;
            }
            TextRenderer.DrawText(g, label, Font, new Rectangle(14, y, labW, RowH), Color.FromArgb(90, 90, 100), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, value, Font, new Rectangle(14 + labW, y, Width - labW - 24, RowH), color, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            y += RowH;
        }

        // measured refresh graph
        y += 8;
        var gr = new Rectangle(12, y, Width - 24, GraphH);
        using (var bg = new SolidBrush(Color.FromArgb(248, 249, 251))) g.FillRectangle(bg, gr);
        using (var bd = new Pen(Color.FromArgb(215, 218, 224))) g.DrawRectangle(bd, gr);
        TextRenderer.DrawText(g, "measured refresh, last 2 min (Hz)", small, new Point(gr.X + 4, gr.Y + 1), Color.Gray);
        int n = Hz.Count;
        if (n >= 2)
        {
            float mx = 1; for (int i = 0; i < n; i++) if (!float.IsNaN(Hz[i])) mx = Math.Max(mx, Hz[i]);
            mx = Math.Max(mx * 1.15f, (float)D.RefreshHz * 1.15f);
            var pts = new List<PointF>();
            for (int i = 0; i < n; i++)
            {
                if (float.IsNaN(Hz[i])) continue;
                pts.Add(new PointF(gr.X + 1 + (float)i * (gr.Width - 2) / Math.Max(1, 119), gr.Bottom - 2 - Hz[i] / mx * (gr.Height - 4)));
            }
            if (pts.Count >= 2) { using var pen = new Pen(Color.SeaGreen, 1.6f); g.DrawLines(pen, pts.ToArray()); }
            TextRenderer.DrawText(g, $"{mx / 1.15f:0} Hz", small, new Rectangle(gr.X, gr.Y + 1, gr.Width - 4, 14), Color.Gray, TextFormatFlags.Right);
        }
    }
}

public sealed class DisplaysView : UserControl
{
    readonly Func<IReadOnlyList<GpuInfo>> _gpus;
    readonly ArrangementMap _map = new() { Dock = DockStyle.Top, Height = 250 };
    readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(4) };
    readonly Dictionary<string, VblankMeter> _meters = new();
    readonly Dictionary<string, DisplayCard> _cards = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    int _tick;
    List<DisplayDesc> _desc = new();

    public DisplaysView(Func<IReadOnlyList<GpuInfo>> gpus)
    {
        _gpus = gpus;
        Controls.Add(_flow);
        Controls.Add(_map);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        HandleDestroyed += (_, _) => { _timer.Stop(); foreach (var m in _meters.Values) m.Stop(); };
        Tick();
    }

    GpuInfo? GpuOf(long luid) => _gpus().FirstOrDefault(g => g.Luid == luid);

    void Tick()
    {
        if (_tick++ % 3 == 0) Refresh_();
        var hz = new Dictionary<string, double>();
        foreach (var (k, m) in _meters)
        {
            double v = m.Failed ? double.NaN : m.ReadHz();
            hz[k] = v;
            if (_cards.TryGetValue(k, out var c)) c.Push(v);
        }
        _map.Set(_desc, GpuOf, hz);
    }

    void Refresh_()
    {
        List<DisplayDesc> d;
        try { d = DisplayTopology.Query(); } catch { return; }
        _desc = d;
        var keys = d.Select(x => x.Key).ToHashSet();
        foreach (var k in _meters.Keys.Where(k => !keys.Contains(k)).ToList()) { _meters[k].Stop(); _meters.Remove(k); }
        foreach (var k in _cards.Keys.Where(k => !keys.Contains(k)).ToList()) { _flow.Controls.Remove(_cards[k]); _cards.Remove(k); }
        foreach (var x in d)
        {
            if (!_meters.ContainsKey(x.Key)) _meters[x.Key] = new VblankMeter(x.AdapterLuid, x.SourceId);
            if (!_cards.TryGetValue(x.Key, out var c)) { _cards[x.Key] = c = new DisplayCard(x); _flow.Controls.Add(c); }
            c.Set(x, GpuOf(x.AdapterLuid));
        }
        // displays with a warning first, then by number
        int pos = 0;
        foreach (var c in _cards.Values.OrderByDescending(c => c.HasWarning).ThenBy(c => c.D.Number))
            _flow.Controls.SetChildIndex(c, pos++);
    }
}
