namespace GpuMon;

/// <summary>NVML-based sampler for Linux hosts (and WSL2). Same sensors as the Windows app, plus per-process VRAM where the driver reports it.</summary>
public sealed class LinuxSampler : IDisposable
{
    public List<GpuInfo> Gpus { get; } = new();
    public Snapshot? Latest { get; private set; }
    public string Status => Nvml.Status;
    public string Driver => Nvml.DriverVersion;
    public bool DockerNames => _lookup.DockerNamesAvailable;
    public int IntervalMs { get; set; } = 1000;

    readonly Dictionary<int, uint> _replay = new();
    readonly ProcLookup _lookup = new();
    Thread? _thread;
    volatile bool _stop;

    public LinuxSampler()
    {
        foreach (var d in Nvml.Init())
        {
            var g = new GpuInfo { Index = d.Index, Luid = d.Index, Name = d.Name, Vendor = "NVIDIA", Nvml = d };
            if (d.Memory() is { } m) g.DedicatedTotal = m.total;
            var l = g.Details;
            l.Add(("Name", g.Name));
            l.Add(("UUID", d.Uuid));
            l.Add(("Driver version", Nvml.DriverVersion));
            l.Add(("VBIOS", d.Vbios));
            l.Add(("Memory", $"{g.DedicatedTotal / 1048576.0:N0} MB"));
            if (d.CudaCapability() is { } cc) l.Add(("CUDA compute capability", $"{cc.major}.{cc.minor}"));
            if (d.Cores() is { } nc) l.Add(("CUDA cores", nc.ToString("N0")));
            if (d.BusWidth() is { } bw) l.Add(("Memory bus width", $"{bw}-bit"));
            if (d.MaxClock(0) is { } mg) l.Add(("Max graphics clock", $"{mg} MHz"));
            if (d.MaxClock(2) is { } mm) l.Add(("Max memory clock", $"{mm} MHz"));
            if (d.PcieMaxGen() is { } pg && d.PcieMaxWidth() is { } pw) l.Add(("PCIe maximum link", $"Gen{pg} x{pw}"));
            if (d.PowerLimitMw() is { } pl) l.Add(("Enforced power limit", $"{pl / 1000.0:N0} W"));
            if (d.TempSlowdown() is { } ts) l.Add(("Thermal slowdown threshold", $"{ts} °C"));
            Gpus.Add(g);
        }
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "gpumon sampler" };
        _thread.Start();
    }

    void Loop()
    {
        while (!_stop)
        {
            try { Latest = Tick(); }
            catch (Exception ex) { Console.Error.WriteLine("sample failed: " + ex.Message); }
            Thread.Sleep(Math.Max(100, IntervalMs));
        }
    }

    public Snapshot Tick()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _lookup.RefreshNames();
        var snap = new Snapshot { Time = DateTime.Now };
        var alive = new HashSet<int>();

        foreach (var g in Gpus)
        {
            var d = g.Nvml!;
            var s = new GpuSample { Gpu = g };
            double totalMb = g.DedicatedTotal / 1048576.0;
            NvmlReader.Read(g, d, s, ref totalMb, _replay);
            s.V["mem.total"] = totalMb;
            if (totalMb > 0 && s.V.TryGetValue("mem.used", out var used)) s.V["mem.pct"] = used / totalMb * 100.0;
            s.DedicatedUsed = (ulong)(s.V.GetValueOrDefault("mem.used") * 1048576.0);

            var util = d.ProcessUtilization();
            var byPid = new Dictionary<int, (ulong mem, string types)>();
            foreach (var (pid, bytes, type) in d.RunningProcesses())
            {
                byPid.TryGetValue(pid, out var cur);
                byPid[pid] = (Math.Max(cur.mem, bytes), cur.types.Contains(type) ? cur.types : cur.types + type);
            }
            foreach (var pid in util.Keys) if (!byPid.ContainsKey(pid)) byPid[pid] = (0, "");

            foreach (var (pid, v) in byPid)
            {
                alive.Add(pid);
                var info = _lookup.Get(pid);
                var p = new ProcSample
                {
                    Pid = pid, Luid = g.Luid, Name = info.Name, Cmd = info.Cmd, Container = info.Container.Length > 0 ? info.Container : info.ContainerId,
                    Dedicated = v.mem, Types = v.types,
                };
                if (util.TryGetValue(pid, out var u)) { p.SmUtil = u.sm; p.MemUtil = u.mem; p.EncUtil = u.enc; p.DecUtil = u.dec; }
                snap.Procs.Add(p);
                s.ProcessDedicatedSum += v.mem;
            }
            snap.Gpus.Add(s);
        }
        _lookup.Prune(alive);
        snap.CollectMs = sw.Elapsed.TotalMilliseconds;
        return snap;
    }

    public void Dispose() { _stop = true; _thread?.Join(2000); }
}
