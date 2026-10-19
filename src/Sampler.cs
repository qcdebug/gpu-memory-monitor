using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GpuMon;

public sealed class Sampler : IDisposable
{
    public event Action<Snapshot>? Updated;
    public volatile int IntervalMs = 1000;
    public volatile bool Paused;
    public List<GpuInfo> Gpus { get; } = new();
    public string NvmlStatus => Nvml.Status;

    readonly PdhQuery _pdh = new();
    IntPtr _cProcDed, _cProcShr, _cProcCom, _cEng, _cAdDed, _cAdShr;
    readonly ProcInfoCache _procs = new();
    readonly Dictionary<int, uint> _lastReplay = new();
    readonly List<KeyValuePair<string, double>> _tmp = new();
    Thread? _thread;
    volatile bool _stop;
    readonly AutoResetEvent _wake = new(false);

    static readonly RegexOptions RO = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex ReProc = new(@"^pid_(\d+)_luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_(\d+)$", RO);
    static readonly Regex ReEng = new(@"^pid_(\d+)_luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.+)$", RO);
    static readonly Regex ReAd = new(@"^luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_(\d+)$", RO);
    static readonly Regex ReTrail = new(@"_\d+$", RO);

    public Sampler()
    {
        Gpus.AddRange(D3dkmt.Enumerate());
        var nv = Nvml.Init();
        // match NVML devices to WDDM adapters by PCI bus number
        foreach (var d in nv)
        {
            int bus = d.PciBus();
            var g = Gpus.FirstOrDefault(x => x.Bus == bus && x.Vendor == "NVIDIA" && x.Nvml == null)
                    ?? Gpus.FirstOrDefault(x => x.Vendor == "NVIDIA" && x.Nvml == null);
            if (g != null) g.Nvml = d;
        }
        foreach (var g in Gpus) BuildDetails(g);

        _cProcDed = _pdh.Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _cProcShr = _pdh.Add(@"\GPU Process Memory(*)\Shared Usage");
        _cProcCom = _pdh.Add(@"\GPU Process Memory(*)\Total Committed");
        _cEng = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _cAdDed = _pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _cAdShr = _pdh.Add(@"\GPU Adapter Memory(*)\Shared Usage");
        _pdh.Collect();   // prime rate counters
    }

    public bool CountersOk => _cProcDed != IntPtr.Zero;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "GpuMon sampler" };
        _thread.Start();
    }

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        while (!_stop)
        {
            long t0 = sw.ElapsedMilliseconds;
            if (!Paused)
            {
                try { Updated?.Invoke(Tick()); } catch (Exception ex) { CrashLog.Note(ex, "sampler tick"); }
            }
            int wait = Math.Max(50, IntervalMs - (int)(sw.ElapsedMilliseconds - t0));
            _wake.WaitOne(wait);
        }
    }

    public void WakeNow() => _wake.Set();

    // ------------------------------------------------------------------------------------------

    static long ParseLuid(string hi, string lo) =>
        ((long)ulong.Parse(hi, NumberStyles.HexNumber) << 32) | (long)ulong.Parse(lo, NumberStyles.HexNumber);

    sealed class ProcAgg
    {
        public ulong Ded, Shr, Com;
        public Dictionary<string, double> Eng = new();
    }

    public Snapshot Tick()
    {
        var sw = Stopwatch.StartNew();
        var snap = new Snapshot { Time = DateTime.Now };
        _pdh.Collect();

        // ---- per-process memory -------------------------------------------------------------
        var procs = new Dictionary<(int pid, long luid), ProcAgg>();
        ProcAgg Agg(int pid, long luid)
        {
            if (!procs.TryGetValue((pid, luid), out var a)) procs[(pid, luid)] = a = new ProcAgg();
            return a;
        }
        void ReadMem(IntPtr c, int which)
        {
            _pdh.Read(c, _tmp);
            foreach (var kv in _tmp)
            {
                var m = ReProc.Match(kv.Key);
                if (!m.Success) continue;
                var a = Agg(int.Parse(m.Groups[1].Value), ParseLuid(m.Groups[2].Value, m.Groups[3].Value));
                ulong v = kv.Value <= 0 ? 0 : (ulong)kv.Value;
                if (which == 0) a.Ded += v; else if (which == 1) a.Shr += v; else a.Com += v;
            }
        }
        ReadMem(_cProcDed, 0); ReadMem(_cProcShr, 1); ReadMem(_cProcCom, 2);

        // ---- engines: sum per (pid, engine) then take the busiest engine of each type -------------
        var procEng = new Dictionary<(int pid, long luid, string type, int eng), double>();
        var adEng = new Dictionary<(long luid, string type, int eng), double>();
        _pdh.Read(_cEng, _tmp);
        foreach (var kv in _tmp)
        {
            var m = ReEng.Match(kv.Key);
            if (!m.Success) continue;
            int pid = int.Parse(m.Groups[1].Value);
            long luid = ParseLuid(m.Groups[2].Value, m.Groups[3].Value);
            int eng = int.Parse(m.Groups[5].Value);
            string type = ReTrail.Replace(m.Groups[6].Value, "");
            procEng[(pid, luid, type, eng)] = procEng.GetValueOrDefault((pid, luid, type, eng)) + kv.Value;
            adEng[(luid, type, eng)] = adEng.GetValueOrDefault((luid, type, eng)) + kv.Value;
        }
        foreach (var kv in procEng)
        {
            var a = Agg(kv.Key.pid, kv.Key.luid);
            a.Eng[kv.Key.type] = Math.Max(a.Eng.GetValueOrDefault(kv.Key.type), kv.Value);
        }
        var adEngMax = new Dictionary<(long luid, string type), double>();
        foreach (var kv in adEng)
            adEngMax[(kv.Key.luid, kv.Key.type)] = Math.Max(adEngMax.GetValueOrDefault((kv.Key.luid, kv.Key.type)), kv.Value);

        // ---- adapter memory -----------------------------------------------------------------
        var adDed = new Dictionary<long, ulong>();
        var adShr = new Dictionary<long, ulong>();
        void ReadAd(IntPtr c, Dictionary<long, ulong> into)
        {
            _pdh.Read(c, _tmp);
            foreach (var kv in _tmp)
            {
                var m = ReAd.Match(kv.Key);
                if (!m.Success) continue;
                long luid = ParseLuid(m.Groups[1].Value, m.Groups[2].Value);
                into[luid] = into.GetValueOrDefault(luid) + (kv.Value <= 0 ? 0 : (ulong)kv.Value);
            }
        }
        ReadAd(_cAdDed, adDed); ReadAd(_cAdShr, adShr);

        // ---- NVML per-process utilisation ---------------------------------------------------------
        var nvProc = new Dictionary<long, Dictionary<int, (uint sm, uint mem, uint enc, uint dec)>>();
        foreach (var g in Gpus.Where(g => g.Nvml != null))
            nvProc[g.Luid] = g.Nvml!.ProcessUtilization();

        // ---- build process samples ----------------------------------------------------------------
        var known = Gpus.Select(g => g.Luid).ToHashSet();
        var alive = new HashSet<int>();
        foreach (var kv in procs)
        {
            if (!known.Contains(kv.Key.luid)) continue;
            alive.Add(kv.Key.pid);
            var info = _procs.Get(kv.Key.pid);
            var p = new ProcSample
            {
                Pid = kv.Key.pid, Luid = kv.Key.luid, Name = info.Name, Path = info.Path, Cmd = info.Cmd,
                Dedicated = kv.Value.Ded, Shared = kv.Value.Shr, Committed = kv.Value.Com, Engines = kv.Value.Eng,
            };
            if (nvProc.TryGetValue(kv.Key.luid, out var nv))
            {
                if (nv.TryGetValue(kv.Key.pid, out var u)) { p.SmUtil = u.sm; p.MemUtil = u.mem; p.EncUtil = u.enc; p.DecUtil = u.dec; }
                else { p.SmUtil = p.MemUtil = p.EncUtil = p.DecUtil = 0; }
            }
            var gi = Gpus.First(x => x.Luid == p.Luid);
            p.Implausible = (gi.DedicatedTotal > 0 && p.Dedicated > gi.DedicatedTotal) ||
                            (gi.SharedTotal > 0 && p.Shared > gi.SharedTotal);
            snap.Procs.Add(p);
        }
        _procs.Prune(alive);

        // ---- per-adapter samples ----------------------------------------------------------------
        foreach (var g in Gpus)
        {
            var s = new GpuSample { Gpu = g };
            s.DedicatedUsed = adDed.GetValueOrDefault(g.Luid);
            s.SharedUsed = adShr.GetValueOrDefault(g.Luid);
            s.ProcessDedicatedSum = (ulong)snap.Procs.Where(p => p.Luid == g.Luid && !p.Implausible).Sum(p => (double)p.Dedicated);
            double totalMb = g.DedicatedTotal / 1048576.0;

            foreach (var kv in adEngMax.Where(k => k.Key.luid == g.Luid))
                s.V["eng." + kv.Key.type] = kv.Value;

            if (g.Nvml is { } d) NvmlReader.Read(g, d, s, ref totalMb, _lastReplay);

            s.V["mem.shared"] = s.SharedUsed / 1048576.0;
            if (!s.V.ContainsKey("mem.used")) { s.V["mem.used"] = s.DedicatedUsed / 1048576.0; }
            s.V["mem.total"] = totalMb;
            if (totalMb > 0) s.V["mem.pct"] = s.V["mem.used"] / totalMb * 100.0;
            if (!s.V.ContainsKey("load.gpu") && s.V.TryGetValue("eng.3D", out var e3)) s.V["load.gpu"] = e3;
            s.DedicatedUsed = (ulong)(s.V["mem.used"] * 1048576.0);
            snap.Gpus.Add(s);
        }
        snap.CollectMs = sw.Elapsed.TotalMilliseconds;
        return snap;
    }

    void BuildDetails(GpuInfo g)
    {
        var l = g.Details;
        l.Add(("Name", g.Name));
        l.Add(("Vendor", g.Vendor));
        l.Add(("WDDM LUID", $"0x{(g.Luid >> 32):X8}_0x{(g.Luid & 0xFFFFFFFF):X8}"));
        if (g.Bus >= 0) l.Add(("PCI location", $"bus {g.Bus}, device {g.Dev}, function {g.Func}"));
        l.Add(("Dedicated video memory", $"{g.DedicatedTotal / 1048576.0:N0} MB"));
        l.Add(("Shared system memory limit", $"{g.SharedTotal / 1048576.0:N0} MB"));
        if (g.Nvml is { } d)
        {
            l.Add(("Driver version", Nvml.DriverVersion));
            l.Add(("NVML version", Nvml.NvmlVersion));
            l.Add(("VBIOS", d.Vbios));
            l.Add(("UUID", d.Uuid));
            if (d.CudaCapability() is { } cc) l.Add(("CUDA compute capability", $"{cc.major}.{cc.minor}"));
            if (d.Cores() is { } nc) l.Add(("CUDA cores", nc.ToString("N0")));
            if (d.BusWidth() is { } bw) l.Add(("Memory bus width", $"{bw}-bit"));
            if (d.MaxClock(0) is { } mg) l.Add(("Max graphics clock", $"{mg} MHz"));
            if (d.MaxClock(2) is { } mm) l.Add(("Max memory clock", $"{mm} MHz"));
            if (d.MaxClock(3) is { } mv) l.Add(("Max video clock", $"{mv} MHz"));
            if (d.PcieMaxGen() is { } pg && d.PcieMaxWidth() is { } pw) l.Add(("PCIe maximum link", $"Gen{pg} x{pw}"));
            if (d.PowerDefaultMw() is { } pd) l.Add(("Default power limit", $"{pd / 1000.0:N0} W"));
            if (d.PowerLimitMw() is { } pl) l.Add(("Enforced power limit", $"{pl / 1000.0:N0} W"));
            if (d.TempSlowdown() is { } ts) l.Add(("Thermal slowdown threshold", $"{ts} °C"));
        }
        else if (g.Vendor == "NVIDIA") l.Add(("Vendor sensors", "NVML unavailable"));
        else l.Add(("Vendor sensors", $"{g.Vendor}: only Windows counters (memory, engine load) are available"));
    }

    public void Dispose()
    {
        _stop = true; _wake.Set();
        _thread?.Join(2000);
        _pdh.Dispose();
    }
}
