using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuMon;

/// <summary>Resolves pid -> name / image path / command line, cached while the pid is alive on a GPU.</summary>
public sealed class ProcInfoCache
{
    public sealed record Info(string Name, string Path, string Cmd);

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder sb, ref int size);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ProcessCommandLineInformation = 60;

    readonly Dictionary<int, Info> _cache = new();

    public Info Get(int pid)
    {
        if (_cache.TryGetValue(pid, out var i)) return i;
        i = Resolve(pid);
        _cache[pid] = i;
        return i;
    }

    /// <summary>Drops cache entries for pids no longer seen (guards against pid reuse).</summary>
    public void Prune(HashSet<int> alive)
    {
        if (_cache.Count < 8) return;
        foreach (var k in _cache.Keys.Where(k => !alive.Contains(k)).ToList()) _cache.Remove(k);
    }

    static Info Resolve(int pid)
    {
        if (pid == 0) return new("System Idle", "", "");
        if (pid == 4) return new("System", "", "");
        string path = "", cmd = "", name = "";
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int sz = sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref sz)) path = sb.ToString();

                IntPtr buf = Marshal.AllocHGlobal(65536);
                try
                {
                    if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, 65536, out _) == 0)
                    {
                        int len = Marshal.ReadInt16(buf);            // UNICODE_STRING.Length (bytes)
                        IntPtr sp = Marshal.ReadIntPtr(buf, 8);       // .Buffer
                        if (len > 0 && sp != IntPtr.Zero) cmd = Marshal.PtrToStringUni(sp, len / 2) ?? "";
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(h); }
        }
        if (path.Length > 0) name = System.IO.Path.GetFileName(path);
        if (name.Length == 0)
        {
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName + ".exe"; }
            catch { name = $"pid {pid}"; }
        }
        // strip the executable itself from the command line so only the arguments remain
        if (cmd.Length > 0)
        {
            string c = cmd.TrimStart();
            if (c.StartsWith('"')) { int e = c.IndexOf('"', 1); c = e > 0 ? c[(e + 1)..] : c; }
            else { int e = c.IndexOf(' '); c = e > 0 ? c[e..] : ""; }
            cmd = c.Trim();
        }
        return new(name, path, cmd);
    }
}
