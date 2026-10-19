using System.Runtime.InteropServices;

namespace GpuMon;

/// <summary>Thin wrapper over the Windows Performance Data Helper, wildcard-instance aware.</summary>
public sealed class PdhQuery : IDisposable
{
    const uint PDH_MORE_DATA = 0x800007D2;
    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_FMT_NOCAP100 = 0x00008000;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(IntPtr c, uint fmt, ref uint bufSize, ref uint itemCount, IntPtr buf);

    IntPtr _q;
    IntPtr _buf = IntPtr.Zero;
    uint _bufSize;

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) _q = IntPtr.Zero;
    }

    public bool Ok => _q != IntPtr.Zero;

    /// <summary>Adds a counter by its English path. Returns IntPtr.Zero if it does not exist on this system.</summary>
    public IntPtr Add(string path)
    {
        if (_q == IntPtr.Zero) return IntPtr.Zero;
        return PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;
    }

    public void Collect() { if (_q != IntPtr.Zero) PdhCollectQueryData(_q); }

    /// <summary>Reads every instance of a wildcard counter as (instance name, value).</summary>
    public void Read(IntPtr counter, List<KeyValuePair<string, double>> into)
    {
        into.Clear();
        if (counter == IntPtr.Zero) return;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint size = _bufSize, count = 0;
            uint r = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, _buf);
            if (r == PDH_MORE_DATA)
            {
                if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                _bufSize = size + 4096;
                _buf = Marshal.AllocHGlobal((int)_bufSize);
                continue;
            }
            if (r != 0) return;
            // PDH_FMT_COUNTERVALUE_ITEM_W on x64: LPWSTR name (8), DWORD CStatus (4) + pad (4), 8-byte value
            const int stride = 24;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = _buf + i * stride;
                uint status = (uint)Marshal.ReadInt32(item, 8);
                if (status != 0 && status != 1) continue;   // not VALID_DATA / NEW_DATA
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                if (name == null) continue;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                into.Add(new(name, v));
            }
            return;
        }
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; }
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
    }
}
