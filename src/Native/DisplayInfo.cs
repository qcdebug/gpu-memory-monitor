using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GpuMon;

public sealed class EdidInfo
{
    public string Mfg = "", Name = "", Serial = "";
    public int ProductCode, Week, Year, WidthCm, HeightCm, Extensions;
    public int NativeW, NativeH; public double NativeHz, NativeClockMHz;
    public bool HasRange; public int VMin, VMax, HMin, HMax, MaxClockMHz;
    public bool HdmiVsdb, HdmiForum; public int HdmiMaxTmdsMHz, HfMaxTmdsMHz;

    public static EdidInfo? Parse(byte[]? e)
    {
        if (e == null || e.Length < 128 || e[0] != 0 || e[1] != 0xFF || e[7] != 0) return null;
        var r = new EdidInfo();
        int m = (e[8] << 8) | e[9];
        r.Mfg = new string(new[] { (char)('A' + ((m >> 10) & 31) - 1), (char)('A' + ((m >> 5) & 31) - 1), (char)('A' + (m & 31) - 1) });
        r.ProductCode = e[10] | (e[11] << 8);
        r.Week = e[16]; r.Year = 1990 + e[17];
        r.WidthCm = e[21]; r.HeightCm = e[22];
        r.Extensions = e[126];
        bool first = true;
        for (int o = 54; o <= 108; o += 18)
        {
            if (e[o] != 0 || e[o + 1] != 0)
            {
                if (!first) continue;
                first = false;
                int clk = (e[o] | (e[o + 1] << 8)) * 10;     // kHz
                int ha = e[o + 2] | ((e[o + 4] & 0xF0) << 4), hb = e[o + 3] | ((e[o + 4] & 0x0F) << 8);
                int va = e[o + 5] | ((e[o + 7] & 0xF0) << 4), vb = e[o + 6] | ((e[o + 7] & 0x0F) << 8);
                r.NativeW = ha; r.NativeH = va; r.NativeClockMHz = clk / 1000.0;
                if ((ha + hb) * (va + vb) > 0) r.NativeHz = clk * 1000.0 / ((ha + hb) * (va + vb));
            }
            else
            {
                byte tag = e[o + 3];
                string Text() => Encoding.ASCII.GetString(e, o + 5, 13).Split('\n')[0].Trim();
                if (tag == 0xFC) r.Name = Text();
                else if (tag == 0xFF) r.Serial = Text();
                else if (tag == 0xFD)
                {
                    r.HasRange = true;
                    r.VMin = e[o + 5]; r.VMax = e[o + 6]; r.HMin = e[o + 7]; r.HMax = e[o + 8]; r.MaxClockMHz = e[o + 9] * 10;
                }
            }
        }
        // CTA-861 extension: look for the HDMI vendor-specific blocks
        for (int x = 1; x <= r.Extensions && (x + 1) * 128 <= e.Length; x++)
        {
            int b = x * 128;
            if (e[b] != 0x02) continue;
            int d = e[b + 2];
            for (int p = b + 4; p < b + d && p < b + 127;)
            {
                int tag = e[p] >> 5, len = e[p] & 0x1F;
                if (tag == 3 && len >= 3 && p + len < b + 128)
                {
                    uint oui = (uint)(e[p + 1] | (e[p + 2] << 8) | (e[p + 3] << 16));
                    if (oui == 0x000C03) { r.HdmiVsdb = true; if (len >= 7) r.HdmiMaxTmdsMHz = e[p + 7] * 5; }
                    else if (oui == 0xC45DD8) { r.HdmiForum = true; if (len >= 5) r.HfMaxTmdsMHz = e[p + 5] * 5; }
                }
                p += len + 1;
            }
        }
        return r;
    }
}

public sealed class DisplayDesc
{
    public long AdapterLuid; public uint SourceId, TargetId;
    public string GdiName = "", MonitorName = "", DevicePath = "", Tech = "", Rotation = "", Scaling = "", ScanOrder = "";
    public bool TechIsHdmi, TechIsDp;
    public int X, Y, W, H; public bool Primary;
    public double RefreshHz, PixelClockMHz, HSyncKHz, VSyncHz;
    public int ActiveW, ActiveH, TotalW, TotalH;
    public bool HdrCapable, HdrEnabled; public int Bpc = 8, Encoding; public double SdrWhiteNits;
    public EdidInfo? Edid;
    public List<int> HzAtRes = new();
    public int MaxHzAtRes;
    public double NeedGbps;
    public string Key => $"{AdapterLuid:X}:{SourceId}:{TargetId}";
    public int Number => int.TryParse(new string(GdiName.Where(char.IsDigit).ToArray()), out var n) ? n : 0;
    public string EncodingName => Encoding switch { 0 => "RGB", 1 => "YCbCr 4:4:4", 2 => "YCbCr 4:2:2", 3 => "YCbCr 4:2:0", _ => "?" };
}

public static class DisplayTopology
{
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint np, out uint nm);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint np, byte[] paths, ref uint nm, byte[] modes, IntPtr topo);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(IntPtr hdr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsExW(string dev, int mode, ref DEVMODE dm, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY; public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    static readonly Dictionary<string, List<(int w, int h, int hz)>> _modeCache = new();
    static readonly Dictionary<string, EdidInfo?> _edidCache = new();
    static string _sig = "";

    static string TechName(uint t) => t switch
    {
        0 => "VGA (HD15)", 1 => "S-Video", 2 => "Composite", 3 => "Component", 4 => "DVI", 5 => "HDMI", 6 => "LVDS", 8 => "D-Terminal", 9 => "SDI",
        10 => "DisplayPort (external)", 11 => "DisplayPort (embedded)", 12 => "UDI (external)", 13 => "UDI (embedded)", 14 => "SDTV dongle",
        15 => "Miracast", 16 => "Indirect (wired)", 17 => "Indirect (virtual)", 18 => "DisplayPort over USB tunnel", 0x80000000 => "Internal", _ => $"Other ({t})",
    };

    static IntPtr Request(int type, int size, uint luidLow, int luidHigh, uint id)
    {
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
        Marshal.WriteInt32(p, 0, type); Marshal.WriteInt32(p, 4, size);
        Marshal.WriteInt32(p, 8, (int)luidLow); Marshal.WriteInt32(p, 12, luidHigh); Marshal.WriteInt32(p, 16, (int)id);
        if (DisplayConfigGetDeviceInfo(p) != 0) { Marshal.FreeHGlobal(p); return IntPtr.Zero; }
        return p;
    }

    public static List<DisplayDesc> Query()
    {
        var res = new List<DisplayDesc>();
        if (GetDisplayConfigBufferSizes(2, out uint np, out uint nm) != 0) return res;
        var paths = new byte[np * 72]; var modes = new byte[nm * 64];
        if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return res;

        for (int i = 0; i < np; i++)
        {
            int p = i * 72;
            uint sLow = BitConverter.ToUInt32(paths, p), sId = BitConverter.ToUInt32(paths, p + 8);
            int sHigh = BitConverter.ToInt32(paths, p + 4);
            uint tLow = BitConverter.ToUInt32(paths, p + 20), tId = BitConverter.ToUInt32(paths, p + 28);
            int tHigh = BitConverter.ToInt32(paths, p + 24);
            uint tech = BitConverter.ToUInt32(paths, p + 36), rot = BitConverter.ToUInt32(paths, p + 40), scal = BitConverter.ToUInt32(paths, p + 44), scan = BitConverter.ToUInt32(paths, p + 56);

            var d = new DisplayDesc
            {
                AdapterLuid = ((long)(uint)sHigh << 32) | sLow, SourceId = sId, TargetId = tId,
                Tech = TechName(tech), TechIsHdmi = tech == 5, TechIsDp = tech is 10 or 11 or 18,
                Rotation = rot switch { 1 => "landscape", 2 => "portrait (90\u00B0)", 3 => "landscape flipped", 4 => "portrait flipped", _ => "?" },
                Scaling = scal switch { 1 => "identity", 2 => "centered", 3 => "stretched", 4 => "aspect-ratio centered", 5 => "custom", _ => "default" },
                ScanOrder = scan switch { 1 => "progressive", 2 or 3 => "interlaced", _ => "unspecified" },
            };

            for (int j = 0; j < nm; j++)
            {
                int m = j * 64;
                uint type = BitConverter.ToUInt32(modes, m), id = BitConverter.ToUInt32(modes, m + 4);
                uint aLow = BitConverter.ToUInt32(modes, m + 8); int aHigh = BitConverter.ToInt32(modes, m + 12);
                long luid = ((long)(uint)aHigh << 32) | aLow;
                if (type == 1 && id == sId && luid == d.AdapterLuid)    // source mode: where it sits on the desktop
                {
                    d.W = (int)BitConverter.ToUInt32(modes, m + 16); d.H = (int)BitConverter.ToUInt32(modes, m + 20);
                    d.X = BitConverter.ToInt32(modes, m + 28); d.Y = BitConverter.ToInt32(modes, m + 32);
                }
                else if (type == 2 && id == tId && luid == d.AdapterLuid)   // target mode: the actual signal timing
                {
                    d.PixelClockMHz = BitConverter.ToUInt64(modes, m + 16) / 1e6;
                    double hn = BitConverter.ToUInt32(modes, m + 24), hd = BitConverter.ToUInt32(modes, m + 28);
                    double vn = BitConverter.ToUInt32(modes, m + 32), vd = BitConverter.ToUInt32(modes, m + 36);
                    d.HSyncKHz = hd > 0 ? hn / hd / 1000.0 : 0;
                    d.VSyncHz = vd > 0 ? vn / vd : 0;
                    d.RefreshHz = d.VSyncHz;
                    d.ActiveW = (int)BitConverter.ToUInt32(modes, m + 40); d.ActiveH = (int)BitConverter.ToUInt32(modes, m + 44);
                    d.TotalW = (int)BitConverter.ToUInt32(modes, m + 48); d.TotalH = (int)BitConverter.ToUInt32(modes, m + 52);
                }
            }
            d.Primary = d.X == 0 && d.Y == 0;

            IntPtr h = Request(1, 84, sLow, sHigh, sId);
            if (h != IntPtr.Zero) { d.GdiName = Marshal.PtrToStringUni(h + 20, 32).TrimEnd('\0'); Marshal.FreeHGlobal(h); }

            h = Request(2, 420, tLow, tHigh, tId);
            if (h != IntPtr.Zero)
            {
                d.MonitorName = Marshal.PtrToStringUni(h + 36, 64).Split('\0')[0];
                d.DevicePath = Marshal.PtrToStringUni(h + 164, 128).Split('\0')[0];
                Marshal.FreeHGlobal(h);
            }
            if (string.IsNullOrWhiteSpace(d.MonitorName)) d.MonitorName = "Generic monitor";

            h = Request(9, 32, tLow, tHigh, tId);
            if (h != IntPtr.Zero)
            {
                uint v = (uint)Marshal.ReadInt32(h, 20);
                d.HdrCapable = (v & 1) != 0; d.HdrEnabled = (v & 2) != 0;
                d.Encoding = Marshal.ReadInt32(h, 24);
                int bpc = Marshal.ReadInt32(h, 28); if (bpc > 0) d.Bpc = bpc;
                Marshal.FreeHGlobal(h);
            }
            h = Request(11, 24, tLow, tHigh, tId);
            if (h != IntPtr.Zero) { d.SdrWhiteNits = (uint)Marshal.ReadInt32(h, 20) / 1000.0 * 80.0; Marshal.FreeHGlobal(h); }

            res.Add(d);
        }

        string sig = string.Join("|", res.Select(r => $"{r.Key}/{r.W}x{r.H}/{r.PixelClockMHz:F0}"));
        if (sig != _sig) { _sig = sig; _modeCache.Clear(); }

        foreach (var d in res)
        {
            // EDID from the registry
            if (!_edidCache.TryGetValue(d.DevicePath, out var ed))
                _edidCache[d.DevicePath] = ed = ReadEdid(d.DevicePath);
            d.Edid = ed;

            // modes Windows offers for this output
            if (!_modeCache.TryGetValue(d.GdiName, out var list))
            {
                list = new();
                var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                for (int k = 0; EnumDisplaySettingsExW(d.GdiName, k, ref dm, 0) && k < 5000; k++)
                    list.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency));
                _modeCache[d.GdiName] = list;
            }
            d.HzAtRes = list.Where(m => m.w == d.W && m.h == d.H).Select(m => m.hz).Distinct().OrderBy(x => x).ToList();
            d.MaxHzAtRes = d.HzAtRes.Count > 0 ? d.HzAtRes.Max() : 0;

            double bpp = d.Encoding switch { 1 => 3, 2 => 2, 3 => 1.5, _ => 3 } * d.Bpc;
            d.NeedGbps = d.PixelClockMHz * 1e6 * bpp / 1e9;
        }
        res.Sort((a, b) => a.Number.CompareTo(b.Number));
        return res;
    }

    static EdidInfo? ReadEdid(string devicePath)
    {
        try
        {
            string s = devicePath.StartsWith(@"\\?\") ? devicePath[4..] : devicePath;
            int g = s.LastIndexOf("#{"); if (g > 0) s = s[..g];
            s = s.Replace('#', '\\');
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{s}\Device Parameters");
            return EdidInfo.Parse(k?.GetValue("EDID") as byte[]);
        }
        catch { return null; }
    }

    /// <summary>Approximate usable video data rates (after line coding) of common links, in Gbps.</summary>
    public static readonly (string Name, double Gbps)[] Links =
    {
        ("HDMI 1.4", 8.16), ("HDMI 2.0", 14.4), ("HDMI 2.1", 42.7),
        ("DP 1.1", 6.48), ("DP 1.2", 17.28), ("DP 1.3/1.4", 25.92), ("DP 2.x UHBR10", 38.7),
    };
}

/// <summary>Counts vertical blanks of one display output on a background thread, giving the real refresh rate.</summary>
public sealed unsafe class VblankMeter
{
    [StructLayout(LayoutKind.Sequential)] struct OpenFromLuid { public uint Low; public int High; public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential)] struct WaitVb { public uint hAdapter; public uint hDevice; public uint Source; }
    [StructLayout(LayoutKind.Sequential)] struct CloseAd { public uint hAdapter; }
    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromLuid(ref OpenFromLuid o);
    [DllImport("gdi32.dll")] static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitVb w);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAd c);

    long _count;
    volatile bool _stop;
    public volatile bool Failed;
    public long Count => Interlocked.Read(ref _count);
    long _lastCount; long _lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    public VblankMeter(long luid, uint source)
    {
        var t = new Thread(() =>
        {
            var o = new OpenFromLuid { Low = (uint)(luid & 0xFFFFFFFF), High = (int)(luid >> 32) };
            if (D3DKMTOpenAdapterFromLuid(ref o) != 0) { Failed = true; return; }
            var w = new WaitVb { hAdapter = o.hAdapter, hDevice = 0, Source = source };
            int errors = 0;
            while (!_stop)
            {
                if (D3DKMTWaitForVerticalBlankEvent(ref w) == 0) { Interlocked.Increment(ref _count); errors = 0; }
                else if (++errors > 5) { Failed = true; Thread.Sleep(2000); }
            }
            var c = new CloseAd { hAdapter = o.hAdapter };
            D3DKMTCloseAdapter(ref c);
        }) { IsBackground = true, Name = "vblank meter" };
        t.Start();
    }

    /// <summary>Vblanks per second since the previous call.</summary>
    public double ReadHz()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), c = Count;
        double secs = (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        double hz = secs > 0.2 ? (c - _lastCount) / secs : double.NaN;
        _lastCount = c; _lastTicks = now;
        return hz;
    }

    public void Stop() => _stop = true;
}
