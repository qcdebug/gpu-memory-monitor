namespace GpuMon;

/// <summary>Static description of one display adapter.</summary>
public sealed class GpuInfo
{
    public int Index;
    public long Luid;
    public string Name = "";
    public string Vendor = "";
    public int Bus = -1, Dev = -1, Func = -1;
    public ulong DedicatedTotal;   // bytes of VRAM
    public ulong SharedTotal;      // bytes of system memory the GPU may use
    public NvmlDevice? Nvml;
    public List<(string Key, string Value)> Details = new();
    public string Label => $"GPU {Index}: {Name}";
}

/// <summary>One adapter's readings for one sampling tick.</summary>
public sealed class GpuSample
{
    public GpuInfo Gpu = null!;
    public Dictionary<string, double> V = new();   // numeric sensors, see SensorDefs
    public Dictionary<string, string> T = new();   // textual sensors
    public ulong DedicatedUsed;                    // best estimate of VRAM in use (bytes)
    public ulong SharedUsed;                       // system memory in use by the GPU (bytes)
    public ulong ProcessDedicatedSum;              // sum of per-process dedicated usage
    public double Get(string k) => V.TryGetValue(k, out var d) ? d : double.NaN;
}

/// <summary>One process on one adapter for one tick.</summary>
public sealed class ProcSample
{
    public int Pid;
    public long Luid;
    public string Name = "";
    public string Path = "";
    public string Cmd = "";
    public string Container = "";   // Linux container the process runs in, when known
    public string Types = "";       // "C" compute and/or "G" graphics (NVML)
    public ulong Dedicated, Shared, Committed;
    /// <summary>Windows reports implausible per-process figures (far above physical VRAM) for some processes; they are shown but excluded from sums.</summary>
    public bool Implausible;
    public Dictionary<string, double> Engines = new();   // normalised engine type -> %
    public double SmUtil = double.NaN, MemUtil = double.NaN, EncUtil = double.NaN, DecUtil = double.NaN;  // NVML
    public double Eng(string t) => Engines.TryGetValue(t, out var d) ? d : 0;
    public double Video => Math.Max(Eng("VideoDecode"), Math.Max(Eng("VideoEncode"), Eng("VideoProcessing")));
}

public sealed class Snapshot
{
    public DateTime Time = DateTime.Now;
    public List<GpuSample> Gpus = new();
    public List<ProcSample> Procs = new();
    public double CollectMs;
}

public sealed class SensorDef
{
    public string Key, Label, Unit, Fmt, MaxKey;
    public double FixedMax;
    public SensorDef(string key, string label, string unit, string fmt, double fixedMax = 0, string maxKey = "")
    { Key = key; Label = label; Unit = unit; Fmt = fmt; FixedMax = fixedMax; MaxKey = maxKey; }
}

public static class Sensors
{
    public static readonly SensorDef[] Defs =
    {
        new("load.gpu",  "GPU Load",                    "%",    "0",    100),
        new("load.mem",  "Memory Controller Load",      "%",    "0",    100),
        new("load.enc",  "Video Encoder Load",          "%",    "0",    100),
        new("load.dec",  "Video Decoder Load",          "%",    "0",    100),
        new("mem.used",  "Memory Used (dedicated VRAM)","MB",   "N0",   0, "mem.total"),
        new("mem.pct",   "Memory Used",                 "%",    "0.0",  100),
        new("mem.shared","Shared Memory Used (system RAM)","MB","N0",   0),
        new("mem.reserved","Memory Reserved (driver)",  "MB",   "N0",   0),
        new("pcie.rx",   "PCIe RX (host -> GPU)",       "MB/s", "N1",   0, "pcie.cap.max"),
        new("pcie.tx",   "PCIe TX (GPU -> host)",       "MB/s", "N1",   0, "pcie.cap.max"),
        new("pcie.util", "PCIe Link Utilisation (busier direction)", "%", "0.0", 100),
        new("pcie.replay","PCIe Replays (per interval)","",     "N0",   0),
        new("clk.gfx",   "GPU Clock",                   "MHz",  "N0",   0, "clk.gfx.max"),
        new("clk.mem",   "Memory Clock",                "MHz",  "N0",   0, "clk.mem.max"),
        new("clk.video", "Video Clock",                 "MHz",  "N0",   0, "clk.video.max"),
        new("temp.gpu",  "GPU Temperature",             "°C", "0", 100),
        new("temp.mem",  "Memory Temperature",          "°C", "0", 110),
        new("fan",       "Fan Speed",                   "%",    "0",    100),
        new("power",     "Board Power Draw",            "W",    "N0",   0, "power.max"),
        new("power.pct", "Power / Limit",               "%",    "0",    100),
    };

    public static readonly (string Key, string Label)[] TextDefs =
    {
        ("pstate",   "Performance State"),
        ("throttle", "Clock Throttle Reason"),
        ("pcie.link","PCIe Link"),
    };

    public static string EngineLabel(string type) => type switch
    {
        "3D" => "Engine: 3D / Graphics",
        "Compute" => "Engine: Compute",
        "Copy" => "Engine: Copy (DMA)",
        "VideoDecode" => "Engine: Video Decode",
        "VideoEncode" => "Engine: Video Encode",
        "VideoProcessing" => "Engine: Video Processing",
        _ => "Engine: " + type,
    };
}
