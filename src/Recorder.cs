using System.Globalization;
using System.Text;

namespace GpuMon;

/// <summary>Appends every sample to two CSV files (adapter sensors, per-process) so intermittent slowdowns can be examined afterwards.</summary>
public sealed class Recorder : IDisposable
{
    StreamWriter? _sens, _proc;
    public string? Folder { get; private set; }
    public bool Active => _sens != null;
    public long Rows { get; private set; }

    static readonly string[] SensorKeys = Sensors.Defs.Select(d => d.Key).Concat(new[] { "mem.total", "eng.3D", "eng.Compute", "eng.Copy", "eng.VideoDecode", "eng.VideoEncode" }).ToArray();

    public void Start()
    {
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GpuMon Logs");
        Directory.CreateDirectory(Folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _sens = new StreamWriter(Path.Combine(Folder, $"gpumon_{stamp}_sensors.csv"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _proc = new StreamWriter(Path.Combine(Folder, $"gpumon_{stamp}_processes.csv"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _sens.WriteLine("time,gpu," + string.Join(",", SensorKeys) + ",throttle,pstate,pcie_link");
        _proc.WriteLine("time,gpu,pid,name,vram_mb,shared_mb,commit_mb,eng_3d,eng_compute,eng_copy,eng_video,nvml_sm,nvml_memctl,implausible");
    }

    static string C(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    static string N(double d) => double.IsNaN(d) ? "" : d.ToString("0.##", CultureInfo.InvariantCulture);

    public void Write(Snapshot s)
    {
        if (_sens == null || _proc == null) return;
        string t = s.Time.ToString("yyyy-MM-dd HH:mm:ss.fff");
        foreach (var g in s.Gpus)
        {
            _sens.WriteLine($"{t},{g.Gpu.Index}," + string.Join(",", SensorKeys.Select(k => g.V.TryGetValue(k, out var v) ? N(v) : "")) +
                            $",{C(g.T.GetValueOrDefault("throttle", ""))},{g.T.GetValueOrDefault("pstate", "")},{C(g.T.GetValueOrDefault("pcie.link", ""))}");
            Rows++;
        }
        foreach (var p in s.Procs)
        {
            if (p.Dedicated < 1048576 && p.Shared < 1048576 && p.Eng("3D") + p.Eng("Compute") + p.Eng("Copy") + p.Video < 0.05) continue;
            int gi = s.Gpus.FirstOrDefault(x => x.Gpu.Luid == p.Luid)?.Gpu.Index ?? -1;
            _proc.WriteLine($"{t},{gi},{p.Pid},{C(p.Name)},{p.Dedicated / 1048576.0:0.#},{p.Shared / 1048576.0:0.#},{p.Committed / 1048576.0:0.#}," +
                            $"{N(p.Eng("3D"))},{N(p.Eng("Compute"))},{N(p.Eng("Copy"))},{N(p.Video)},{N(p.SmUtil)},{N(p.MemUtil)},{(p.Implausible ? 1 : 0)}");
        }
    }

    public void Stop() { _sens?.Dispose(); _proc?.Dispose(); _sens = _proc = null; }
    public void Dispose() => Stop();
}
