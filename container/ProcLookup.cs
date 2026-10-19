using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GpuMon;

/// <summary>
/// Resolves a host PID to a name, command line and owning container using /proc (needs --pid=host) and,
/// optionally, the Docker socket for container names.
/// </summary>
public sealed class ProcLookup
{
    public sealed record Info(string Name, string Cmd, string ContainerId, string Container, bool Visible);

    static readonly Regex ReId = new(@"([0-9a-f]{64})", RegexOptions.Compiled);
    readonly Dictionary<int, Info> _cache = new();
    Dictionary<string, string> _names = new();
    DateTime _namesAt = DateTime.MinValue;
    readonly string _socket = Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock";

    public bool DockerNamesAvailable { get; private set; }

    public Info Get(int pid)
    {
        if (_cache.TryGetValue(pid, out var i) && (i.Visible || (DateTime.UtcNow.Second % 10) != 0)) return Refreshed(pid, i);
        i = Resolve(pid);
        _cache[pid] = i;
        return i;
    }

    // container names can appear after a process was first cached
    Info Refreshed(int pid, Info i)
    {
        if (i.ContainerId.Length > 0 && i.Container.Length == 0 && _names.TryGetValue(i.ContainerId, out var n))
            _cache[pid] = i = i with { Container = n };
        return i;
    }

    public void Prune(HashSet<int> alive)
    {
        if (_cache.Count < 16) return;
        foreach (var k in _cache.Keys.Where(k => !alive.Contains(k)).ToList()) _cache.Remove(k);
    }

    Info Resolve(int pid)
    {
        string dir = $"/proc/{pid}";
        if (!Directory.Exists(dir)) return new($"pid {pid}", "", "", "", false);
        string name = Read($"{dir}/comm").Trim();
        string cmd = Read($"{dir}/cmdline").Replace('\0', ' ').Trim();
        string exe = "";
        try { exe = new FileInfo($"{dir}/exe").LinkTarget ?? ""; } catch { }
        if (exe.Length > 0) name = Path.GetFileName(exe.Replace(" (deleted)", ""));
        if (name.Length == 0) name = $"pid {pid}";

        string id = "", cname = "";
        var m = ReId.Match(Read($"{dir}/cgroup"));
        if (m.Success)
        {
            id = m.Value[..12];
            _names.TryGetValue(id, out cname!);
            cname ??= "";
        }
        return new(name, cmd, id, cname, true);
    }

    static string Read(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    /// <summary>Refreshes container id -> name from the Docker API every 15 s. Silent if the socket is not mounted.</summary>
    public void RefreshNames()
    {
        if ((DateTime.UtcNow - _namesAt).TotalSeconds < 15) return;
        _namesAt = DateTime.UtcNow;
        if (!File.Exists(_socket)) { DockerNamesAvailable = false; return; }
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(_socket), ct);
                    return new NetworkStream(s, ownsSocket: true);
                }
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            string json = http.GetStringAsync("http://localhost/containers/json").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, string>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                string id = c.GetProperty("Id").GetString() ?? "";
                string n = c.TryGetProperty("Names", out var ns) && ns.GetArrayLength() > 0 ? (ns[0].GetString() ?? "").TrimStart('/') : "";
                if (id.Length >= 12) map[id[..12]] = n;
            }
            _names = map;
            DockerNamesAvailable = true;
        }
        catch { DockerNamesAvailable = false; }
    }
}
