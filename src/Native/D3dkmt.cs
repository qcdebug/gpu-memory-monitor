using System.Runtime.InteropServices;

namespace GpuMon;

/// <summary>Enumerates display adapters through the WDDM kernel-mode thunks (gdi32), giving LUID, PCI bus address, name and memory sizes.</summary>
public static unsafe class D3dkmt
{
    [StructLayout(LayoutKind.Sequential)]
    struct AdapterInfo { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int PresentMove; }
    [StructLayout(LayoutKind.Sequential)]
    struct EnumAdapters2 { public uint NumAdapters; public AdapterInfo* pAdapters; }
    [StructLayout(LayoutKind.Sequential)]
    struct QueryAdapterInfo { public uint hAdapter; public uint Type; public void* pData; public uint Size; }
    [StructLayout(LayoutKind.Sequential)]
    struct CloseAdapter { public uint hAdapter; }

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 a);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo q);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter c);

    const uint KMTQAITYPE_GETSEGMENTSIZE = 3;
    const uint KMTQAITYPE_ADAPTERADDRESS = 6;
    const uint KMTQAITYPE_ADAPTERREGISTRYINFO = 8;

    static bool Query(uint h, uint type, void* buf, uint size)
    {
        var q = new QueryAdapterInfo { hAdapter = h, Type = type, pData = buf, Size = size };
        return D3DKMTQueryAdapterInfo(ref q) == 0;
    }

    public static List<GpuInfo> Enumerate()
    {
        var list = new List<GpuInfo>();
        var e = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return list;
        var arr = new AdapterInfo[e.NumAdapters];
        var reg = stackalloc char[260 * 4];
        uint* addr = stackalloc uint[4];
        ulong* seg = stackalloc ulong[3];
        fixed (AdapterInfo* p = arr)
        {
            e.pAdapters = p;
            if (D3DKMTEnumAdapters2(ref e) != 0) return list;
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var a = arr[i];
                var g = new GpuInfo { Luid = ((long)(uint)a.LuidHigh << 32) | a.LuidLow };

                new Span<char>(reg, 260 * 4).Clear(); new Span<uint>(addr, 4).Clear(); new Span<ulong>(seg, 3).Clear();
                if (Query(a.hAdapter, KMTQAITYPE_ADAPTERREGISTRYINFO, reg, 260 * 4 * 2))
                    g.Name = new string(reg).Trim();

                if (Query(a.hAdapter, KMTQAITYPE_ADAPTERADDRESS, addr, 12) || Query(a.hAdapter, KMTQAITYPE_ADAPTERADDRESS, addr, 16))
                { g.Bus = (int)addr[0]; g.Dev = (int)addr[1]; g.Func = (int)addr[2]; }

                if (Query(a.hAdapter, KMTQAITYPE_GETSEGMENTSIZE, seg, 24))
                { g.DedicatedTotal = seg[0]; g.SharedTotal = seg[2]; }

                var c = new CloseAdapter { hAdapter = a.hAdapter };
                D3DKMTCloseAdapter(ref c);

                if (string.IsNullOrWhiteSpace(g.Name) && g.Bus < 0 && g.DedicatedTotal == 0) continue;  // virtual/indirect display adapters
                if (string.IsNullOrWhiteSpace(g.Name)) g.Name = "Unknown adapter";
                if (g.Name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;  // Basic Render / Basic Display
                string n = g.Name.ToUpperInvariant();
                g.Vendor = n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("QUADRO") ? "NVIDIA"
                         : n.Contains("AMD") || n.Contains("RADEON") ? "AMD"
                         : n.Contains("INTEL") ? "Intel" : "Unknown";
                list.Add(g);
            }
        }
        list.Sort((x, y) => x.Bus != y.Bus ? x.Bus.CompareTo(y.Bus) : x.Luid.CompareTo(y.Luid));
        for (int i = 0; i < list.Count; i++) list[i].Index = i;
        return list;
    }
}
