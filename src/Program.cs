using System.Text;

namespace GpuMon;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--crash-test") { CrashLog.Note(new InvalidOperationException("crash-report self test"), "self test"); return; }
        if (args.Length >= 2 && args[0] == "--dump") { Dump(args[1]); return; }
        ApplicationConfiguration.Initialize();
        CrashLog.Install();
        Application.Run(new MainForm(args));
    }

    /// <summary>Headless self-test: writes a few samples as text so data collection can be checked without the UI.</summary>
    static void Dump(string path)
    {
        var sb = new StringBuilder();
        using var s = new Sampler();
        sb.AppendLine($"NVML: {s.NvmlStatus}   counters ok: {s.CountersOk}");
        foreach (var g in s.Gpus)
        {
            sb.AppendLine($"[{g.Label}] luid=0x{g.Luid:X} bus={g.Bus} nvml={(g.Nvml != null)}");
            foreach (var (k, v) in g.Details) sb.AppendLine($"    {k}: {v}");
        }
        Snapshot snap = null!;
        for (int i = 0; i < 4; i++) { Thread.Sleep(1000); snap = s.Tick(); }
        sb.AppendLine($"collect {snap.CollectMs:F1} ms");
        foreach (var g in snap.Gpus)
        {
            sb.AppendLine($"== {g.Gpu.Label}  used={g.DedicatedUsed / 1048576} MB  procSum={g.ProcessDedicatedSum / 1048576} MB  shared={g.SharedUsed / 1048576} MB");
            foreach (var kv in g.V.OrderBy(k => k.Key)) sb.AppendLine($"    {kv.Key} = {kv.Value:F1}");
            foreach (var kv in g.T) sb.AppendLine($"    {kv.Key} = {kv.Value}");
        }
        foreach (var p in snap.Procs.OrderByDescending(p => p.Dedicated).Take(25))
            sb.AppendLine($"  pid {p.Pid,6} gpu{s.Gpus.First(g => g.Luid == p.Luid).Index} {p.Name,-28} ded={p.Dedicated / 1048576,6} shr={p.Shared / 1048576,6} com={p.Committed / 1048576,6} " +
                          $"3D={p.Eng("3D"):F1} cmp={p.Eng("Compute"):F1} cpy={p.Eng("Copy"):F1} vid={p.Video:F1} sm={p.SmUtil} mem={p.MemUtil}  {Trunc(p.Cmd, 60)}");
        sb.AppendLine("== displays");
        var meters = new Dictionary<string, VblankMeter>();
        var ds = DisplayTopology.Query();
        foreach (var d in ds) meters[d.Key] = new VblankMeter(d.AdapterLuid, d.SourceId);
        Thread.Sleep(1500); foreach (var m in meters.Values) m.ReadHz(); Thread.Sleep(2000);
        foreach (var d in ds)
        {
            sb.AppendLine($"  {d.GdiName} {d.MonitorName} gpuLuid=0x{d.AdapterLuid:X} src={d.SourceId} {d.Tech} {d.W}x{d.H}@({d.X},{d.Y}) mode={d.RefreshHz:F3}Hz measured={(meters[d.Key].Failed ? "FAILED" : meters[d.Key].ReadHz().ToString("F2"))}Hz");
            sb.AppendLine($"    pclk={d.PixelClockMHz:F2}MHz hsync={d.HSyncKHz:F2}kHz active={d.ActiveW}x{d.ActiveH} total={d.TotalW}x{d.TotalH} bpc={d.Bpc} enc={d.EncodingName} hdr={d.HdrCapable}/{d.HdrEnabled} need={d.NeedGbps:F1}Gbps");
            sb.AppendLine($"    offered@res: {string.Join(",", d.HzAtRes)}  edid: {(d.Edid == null ? "none" : $"{d.Edid.Mfg}{d.Edid.ProductCode:X4} {d.Edid.Name} native {d.Edid.NativeW}x{d.Edid.NativeH}@{d.Edid.NativeHz:F1} range V{d.Edid.VMin}-{d.Edid.VMax} H{d.Edid.HMin}-{d.Edid.HMax} maxclk {d.Edid.MaxClockMHz} ext={d.Edid.Extensions}")}");
        }
        File.WriteAllText(path, sb.ToString());
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
