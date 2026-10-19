using System.Runtime.InteropServices;
using System.Text;

namespace GpuMon;

/// <summary>Dynamic binding to nvml.dll (ships with the NVIDIA driver). Everything degrades gracefully when absent.</summary>
public static unsafe class Nvml
{
    static IntPtr _lib;
    static readonly Dictionary<string, IntPtr> _fn = new();
    public static bool Available { get; private set; }
    public static string Status { get; private set; } = "not initialised";
    public static string DriverVersion { get; private set; } = "";
    public static string NvmlVersion { get; private set; } = "";

    internal static IntPtr Fn(string name)
    {
        if (_fn.TryGetValue(name, out var p)) return p;
        if (_lib == IntPtr.Zero || !NativeLibrary.TryGetExport(_lib, name, out p)) p = IntPtr.Zero;
        _fn[name] = p;
        return p;
    }

    public static List<NvmlDevice> Init()
    {
        var devs = new List<NvmlDevice>();
        string[] cands = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.SystemDirectory, "nvml.dll"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll", "nvml.dll" }
            : new[] { "libnvidia-ml.so.1", "/usr/lib/wsl/lib/libnvidia-ml.so.1", "/usr/lib/x86_64-linux-gnu/libnvidia-ml.so.1", "/usr/lib64/libnvidia-ml.so.1", "libnvidia-ml.so" };
        foreach (var c in cands) if (NativeLibrary.TryLoad(c, out _lib)) break;
        if (_lib == IntPtr.Zero) { Status = "nvml.dll not found (no NVIDIA driver)"; return devs; }

        var init = Fn("nvmlInit_v2");
        if (init == IntPtr.Zero) init = Fn("nvmlInit");
        if (init == IntPtr.Zero) { Status = "nvml.dll has no nvmlInit"; return devs; }
        int r = ((delegate* unmanaged[Cdecl]<int>)init)();
        if (r != 0) { Status = $"nvmlInit failed ({r})"; return devs; }
        Available = true;

        DriverVersion = Str("nvmlSystemGetDriverVersion", 80);
        NvmlVersion = Str("nvmlSystemGetNVMLVersion", 80);

        var cnt = Fn("nvmlDeviceGetCount_v2");
        var get = Fn("nvmlDeviceGetHandleByIndex_v2");
        uint n = 0;
        if (cnt != IntPtr.Zero && get != IntPtr.Zero && ((delegate* unmanaged[Cdecl]<uint*, int>)cnt)(&n) == 0)
        {
            for (uint i = 0; i < n; i++)
            {
                IntPtr h;
                if (((delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)get)(i, &h) == 0) devs.Add(new NvmlDevice(h, (int)i));
            }
        }
        Status = $"NVML {NvmlVersion}, driver {DriverVersion}, {devs.Count} device(s)";
        return devs;
    }

    static string Str(string fn, int len)
    {
        var p = Fn(fn);
        if (p == IntPtr.Zero) return "";
        var b = stackalloc byte[len];
        return ((delegate* unmanaged[Cdecl]<byte*, uint, int>)p)(b, (uint)len) == 0 ? Encoding.ASCII.GetString(b, StrLen(b, len)) : "";
    }

    internal static int StrLen(byte* b, int max) { int i = 0; while (i < max && b[i] != 0) i++; return i; }
}

public sealed unsafe class NvmlDevice
{
    public readonly IntPtr H;
    public readonly int Index;
    ulong _lastSeen;

    public NvmlDevice(IntPtr h, int index) { H = h; Index = index; }

    // ---- generic getters -------------------------------------------------------------------
    public uint? U(string fn)
    {
        var p = Nvml.Fn(fn); if (p == IntPtr.Zero) return null;
        uint v;
        return ((delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)p)(H, &v) == 0 ? v : null;
    }

    public uint? U1(string fn, uint arg)
    {
        var p = Nvml.Fn(fn); if (p == IntPtr.Zero) return null;
        uint v;
        return ((delegate* unmanaged[Cdecl]<IntPtr, uint, uint*, int>)p)(H, arg, &v) == 0 ? v : null;
    }

    public string Str(string fn, int len = 96)
    {
        var p = Nvml.Fn(fn); if (p == IntPtr.Zero) return "";
        var b = stackalloc byte[len];
        return ((delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)p)(H, b, (uint)len) == 0
            ? Encoding.ASCII.GetString(b, Nvml.StrLen(b, len)) : "";
    }

    // ---- specific queries ------------------------------------------------------------------
    public string Name => Str("nvmlDeviceGetName");
    public string Uuid => Str("nvmlDeviceGetUUID");
    public string Vbios => Str("nvmlDeviceGetVbiosVersion");

    public int PciBus()
    {
        var p = Nvml.Fn("nvmlDeviceGetPciInfo_v3"); if (p == IntPtr.Zero) return -1;
        var b = stackalloc byte[128];
        if (((delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)p)(H, b) != 0) return -1;
        return (int)*(uint*)(b + 20);   // busIdLegacy[16], domain, then bus
    }

    public uint? Temp() => U1("nvmlDeviceGetTemperature", 0);
    public uint? TempSlowdown() => U1("nvmlDeviceGetTemperatureThreshold", 1);
    public uint? Fan() => U("nvmlDeviceGetFanSpeed");
    public uint? PowerMw() => U("nvmlDeviceGetPowerUsage");
    public uint? PowerLimitMw() => U("nvmlDeviceGetEnforcedPowerLimit");
    public uint? PowerDefaultMw() => U("nvmlDeviceGetPowerManagementDefaultLimit");
    public uint? Clock(uint type) => U1("nvmlDeviceGetClockInfo", type);       // 0 gfx, 1 sm, 2 mem, 3 video
    public uint? MaxClock(uint type) => U1("nvmlDeviceGetMaxClockInfo", type);
    public uint? PcieKBps(uint dir) => U1("nvmlDeviceGetPcieThroughput", dir); // 0 = TX (GPU->host), 1 = RX
    public uint? PcieGen() => U("nvmlDeviceGetCurrPcieLinkGeneration");
    public uint? PcieWidth() => U("nvmlDeviceGetCurrPcieLinkWidth");
    public uint? PcieMaxGen() => U("nvmlDeviceGetMaxPcieLinkGeneration");
    public uint? PcieMaxWidth() => U("nvmlDeviceGetMaxPcieLinkWidth");
    public uint? PcieReplay() => U("nvmlDeviceGetPcieReplayCounter");
    public uint? PState() => U("nvmlDeviceGetPerformanceState");
    public uint? BusWidth() => U("nvmlDeviceGetMemoryBusWidth");
    public uint? Cores() => U("nvmlDeviceGetNumGpuCores");

    public (uint gpu, uint mem)? Utilization()
    {
        var p = Nvml.Fn("nvmlDeviceGetUtilizationRates"); if (p == IntPtr.Zero) return null;
        uint* v = stackalloc uint[2];
        return ((delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)p)(H, v) == 0 ? (v[0], v[1]) : null;
    }

    public uint? EncoderUtil() => Codec("nvmlDeviceGetEncoderUtilization");
    public uint? DecoderUtil() => Codec("nvmlDeviceGetDecoderUtilization");
    uint? Codec(string fn)
    {
        var p = Nvml.Fn(fn); if (p == IntPtr.Zero) return null;
        uint a, b;
        return ((delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int>)p)(H, &a, &b) == 0 ? a : null;
    }

    /// <summary>Total / used / reserved bytes. Uses the v2 call (which accounts for driver reservation) when available.</summary>
    public (ulong total, ulong used, ulong reserved)? Memory()
    {
        var p2 = Nvml.Fn("nvmlDeviceGetMemoryInfo_v2");
        if (p2 != IntPtr.Zero)
        {
            byte* b = stackalloc byte[40];
            new Span<byte>(b, 40).Clear();
            *(uint*)b = 40u | (2u << 24);
            if (((delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)p2)(H, b) == 0)
                return (*(ulong*)(b + 8), *(ulong*)(b + 32), *(ulong*)(b + 16));
        }
        var p = Nvml.Fn("nvmlDeviceGetMemoryInfo"); if (p == IntPtr.Zero) return null;
        ulong* m = stackalloc ulong[3];
        return ((delegate* unmanaged[Cdecl]<IntPtr, ulong*, int>)p)(H, m) == 0 ? (m[0], m[2], 0) : null;
    }

    public ulong? ThrottleMask()
    {
        var p = Nvml.Fn("nvmlDeviceGetCurrentClocksEventReasons");
        if (p == IntPtr.Zero) p = Nvml.Fn("nvmlDeviceGetCurrentClocksThrottleReasons");
        if (p == IntPtr.Zero) return null;
        ulong v;
        return ((delegate* unmanaged[Cdecl]<IntPtr, ulong*, int>)p)(H, &v) == 0 ? v : null;
    }

    public static string ThrottleText(ulong m)
    {
        var parts = new List<string>();
        if ((m & 0x04) != 0) parts.Add("SW power cap");
        if ((m & 0x08) != 0) parts.Add("HW slowdown");
        if ((m & 0x20) != 0) parts.Add("SW thermal");
        if ((m & 0x40) != 0) parts.Add("HW thermal");
        if ((m & 0x80) != 0) parts.Add("HW power brake");
        if ((m & 0x10) != 0) parts.Add("sync boost");
        if ((m & 0x02) != 0) parts.Add("app clocks");
        if ((m & 0x100) != 0) parts.Add("display clocks");
        if (parts.Count == 0) return (m & 0x01) != 0 ? "None (idle)" : "None";
        return string.Join(", ", parts);
    }

    public (int major, int minor)? CudaCapability()
    {
        var p = Nvml.Fn("nvmlDeviceGetCudaComputeCapability"); if (p == IntPtr.Zero) return null;
        int a, b;
        return ((delegate* unmanaged[Cdecl]<IntPtr, int*, int*, int>)p)(H, &a, &b) == 0 ? (a, b) : null;
    }

    /// <summary>GDDR junction temperature via the field-value API (not supported on every board).</summary>
    public uint? MemoryTemp()
    {
        var p = Nvml.Fn("nvmlDeviceGetFieldValues"); if (p == IntPtr.Zero) return null;
        byte* b = stackalloc byte[40];
        new Span<byte>(b, 40).Clear();
        *(uint*)b = 82;   // NVML_FI_DEV_MEMORY_TEMP
        if (((delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)p)(H, 1, b) != 0) return null;
        if (*(int*)(b + 28) != 0) return null;
        uint type = *(uint*)(b + 24);
        return type == 1 ? *(uint*)(b + 32) : type == 5 ? (uint)*(int*)(b + 32) : null;
    }

    /// <summary>Processes holding GPU memory according to the driver (compute + graphics). Memory is 0 when the driver does not report it (WDDM / WSL2).</summary>
    public List<(int pid, ulong usedBytes, string type)> RunningProcesses()
    {
        var res = new List<(int, ulong, string)>();
        foreach (var (fn, type) in new[] { ("nvmlDeviceGetComputeRunningProcesses_v3", "C"), ("nvmlDeviceGetGraphicsRunningProcesses_v3", "G") })
        {
            var p = Nvml.Fn(fn); if (p == IntPtr.Zero) continue;
            var f = (delegate* unmanaged[Cdecl]<IntPtr, uint*, byte*, int>)p;
            uint count = 0;
            int r = f(H, &count, null);
            if ((r != 0 && r != 7) || count == 0) continue;     // 7 = insufficient size, i.e. there are processes
            count += 8;
            var buf = new byte[count * 24];                     // nvmlProcessInfo_v2: pid, usedGpuMemory, gpuInstanceId, computeInstanceId
            fixed (byte* b = buf)
            {
                if (f(H, &count, b) != 0) continue;
                for (uint i = 0; i < count; i++)
                {
                    ulong used = *(ulong*)(b + i * 24 + 8);
                    res.Add(((int)*(uint*)(b + i * 24), used == ulong.MaxValue ? 0 : used, type));
                }
            }
        }
        return res;
    }

    /// <summary>Per-process SM / memory-controller / encoder / decoder utilisation since the previous call.</summary>
    public Dictionary<int, (uint sm, uint mem, uint enc, uint dec)> ProcessUtilization()
    {
        var res = new Dictionary<int, (uint, uint, uint, uint)>();
        var p = Nvml.Fn("nvmlDeviceGetProcessUtilization"); if (p == IntPtr.Zero) return res;
        var f = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint*, ulong, int>)p;
        uint count = 0;
        int r = f(H, null, &count, _lastSeen);
        if ((r != 0 && r != 7) || count == 0) return res;
        count += 32;
        var buf = new byte[count * 32];
        fixed (byte* b = buf)
        {
            if (f(H, b, &count, _lastSeen) != 0) return res;
            var best = new Dictionary<int, ulong>();
            ulong maxTs = _lastSeen;
            for (uint i = 0; i < count; i++)
            {
                byte* s = b + i * 32;
                int pid = (int)*(uint*)s;
                ulong ts = *(ulong*)(s + 8);
                if (ts > maxTs) maxTs = ts;
                if (best.TryGetValue(pid, out var old) && old >= ts) continue;
                best[pid] = ts;
                res[pid] = (*(uint*)(s + 16), *(uint*)(s + 20), *(uint*)(s + 24), *(uint*)(s + 28));
            }
            _lastSeen = maxTs;
        }
        return res;
    }
}
