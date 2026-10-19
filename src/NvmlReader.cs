namespace GpuMon;

/// <summary>Reads every NVML sensor into a GpuSample. Shared by the Windows app and the Linux container.</summary>
public static class NvmlReader
{
    public static void Read(GpuInfo g, NvmlDevice d, GpuSample s, ref double totalMb, Dictionary<int, uint> lastReplay)
    {
        var u = d.Utilization();
        if (u is { } uu) { s.V["load.gpu"] = uu.gpu; s.V["load.mem"] = uu.mem; }
        if (d.EncoderUtil() is { } enc) s.V["load.enc"] = enc;
        if (d.DecoderUtil() is { } dec) s.V["load.dec"] = dec;
        if (d.Memory() is { } m)
        {
            s.V["mem.used"] = m.used / 1048576.0;
            totalMb = m.total / 1048576.0;
            if (m.reserved > 0) s.V["mem.reserved"] = m.reserved / 1048576.0;
        }
        if (d.PcieKBps(1) is { } rx) s.V["pcie.rx"] = rx / 1024.0;
        if (d.PcieKBps(0) is { } tx) s.V["pcie.tx"] = tx / 1024.0;
        if (d.PcieReplay() is { } rp)
        {
            s.V["pcie.replay"] = lastReplay.TryGetValue(g.Index, out var prev) ? rp - prev : 0;
            lastReplay[g.Index] = rp;
        }
        if (d.Clock(0) is { } cg) s.V["clk.gfx"] = cg;
        if (d.Clock(2) is { } cm) s.V["clk.mem"] = cm;
        if (d.Clock(3) is { } cv) s.V["clk.video"] = cv;
        if (d.MaxClock(0) is { } mg) s.V["clk.gfx.max"] = mg;
        if (d.MaxClock(2) is { } mm) s.V["clk.mem.max"] = mm;
        if (d.MaxClock(3) is { } mv) s.V["clk.video.max"] = mv;
        if (d.Temp() is { } t) s.V["temp.gpu"] = t;
        if (d.MemoryTemp() is { } mt) s.V["temp.mem"] = mt;
        if (d.Fan() is { } f) s.V["fan"] = f;
        if (d.PowerMw() is { } pw)
        {
            s.V["power"] = pw / 1000.0;
            if (d.PowerLimitMw() is { } pl && pl > 0) { s.V["power.max"] = pl / 1000.0; s.V["power.pct"] = pw * 100.0 / pl; }
        }
        if (d.PState() is { } ps) s.T["pstate"] = "P" + ps;
        if (d.ThrottleMask() is { } tm) s.T["throttle"] = NvmlDevice.ThrottleText(tm);
        if (d.PcieGen() is { } gen && d.PcieWidth() is { } w)
        {
            s.V["pcie.gen"] = gen; s.V["pcie.width"] = w;
            s.V["pcie.cap"] = LinkMBps(gen, w);
            uint mg2 = d.PcieMaxGen() ?? gen, mw2 = d.PcieMaxWidth() ?? w;
            s.V["pcie.maxgen"] = mg2; s.V["pcie.maxwidth"] = mw2;
            s.V["pcie.cap.max"] = LinkMBps(mg2, mw2);
            if (s.V.TryGetValue("pcie.rx", out var rxv) && s.V.TryGetValue("pcie.tx", out var txv))
                s.V["pcie.util"] = Math.Min(100, Math.Max(rxv, txv) / s.V["pcie.cap"] * 100);
            string max = d.PcieMaxGen() is { } mgn && d.PcieMaxWidth() is { } mw ? $"  (max Gen{mgn} x{mw})" : "";
            s.T["pcie.link"] = $"Gen{gen} x{w}{max}";
        }
    }

    /// <summary>Usable PCIe bandwidth per direction in MB/s (after line encoding), by generation and lane count.</summary>
    public static double LinkMBps(uint gen, uint lanes)
    {
        double perLane = gen switch { 1 => 250, 2 => 500, 3 => 984.6, 4 => 1969.2, 5 => 3938.4, 6 => 7563, _ => 250 };
        return perLane * lanes;
    }
}
