using System.Globalization;
using System.Text;
using GpuMon;

// --healthcheck: used by the Docker HEALTHCHECK, no curl needed in the image
if (args.Length > 0 && args[0] == "--healthcheck")
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string port = (Environment.GetEnvironmentVariable("GPUMON_PORT") ?? "9102");
        var r = http.GetAsync($"http://127.0.0.1:{port}/healthz").GetAwaiter().GetResult();
        return r.IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
// GPUMON_URLS wins; otherwise honour ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS if set (the Dockerfile sets the latter); else GPUMON_PORT or 9102
string urls = Environment.GetEnvironmentVariable("GPUMON_URLS")
              ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
              ?? (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") is { Length: > 0 } p ? $"http://0.0.0.0:{p}" : null)
              ?? $"http://0.0.0.0:{Environment.GetEnvironmentVariable("GPUMON_PORT") ?? "9102"}";
if (Environment.GetEnvironmentVariable("GPUMON_URLS") != null) builder.WebHost.UseUrls(urls);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

using var sampler = new LinuxSampler();
if (int.TryParse(Environment.GetEnvironmentVariable("GPUMON_INTERVAL_MS"), out var ms)) sampler.IntervalMs = Math.Clamp(ms, 250, 60000);
sampler.Start();
Console.WriteLine($"gpumon: {sampler.Status}; listening on {urls}");

app.UseDefaultFiles();
app.UseStaticFiles();

static double? N(double d) => double.IsNaN(d) || double.IsInfinity(d) ? null : Math.Round(d, 3);

app.MapGet("/healthz", () => sampler.Latest != null ? Results.Ok("ok") : Results.StatusCode(503));

app.MapGet("/api/snapshot", () =>
{
    var s = sampler.Latest;
    return Results.Json(new
    {
        time = s?.Time,
        collectMs = s == null ? 0 : Math.Round(s.CollectMs, 1),
        nvml = sampler.Status,
        driver = sampler.Driver,
        dockerNames = sampler.DockerNames,
        defs = Sensors.Defs.Select(d => new { key = d.Key, label = d.Label, unit = d.Unit, fmt = d.Fmt, fixedMax = d.FixedMax, maxKey = d.MaxKey }),
        textDefs = Sensors.TextDefs.Select(t => new { key = t.Key, label = t.Label }),
        gpus = (s?.Gpus ?? new()).Select(g => new
        {
            index = g.Gpu.Index,
            name = g.Gpu.Name,
            details = g.Gpu.Details.Select(x => new[] { x.Key, x.Value }),
            v = g.V.Where(k => !double.IsNaN(k.Value)).ToDictionary(k => k.Key, k => N(k.Value)),
            t = g.T,
            procs = s!.Procs.Where(p => p.Luid == g.Gpu.Luid).OrderByDescending(p => p.Dedicated).Select(p => new
            {
                pid = p.Pid, name = p.Name, cmd = p.Cmd, container = p.Container, types = p.Types,
                memMb = Math.Round(p.Dedicated / 1048576.0, 1), sm = N(p.SmUtil), mem = N(p.MemUtil), enc = N(p.EncUtil), dec = N(p.DecUtil),
            }),
        }),
    });
});

app.MapGet("/metrics", () => Results.Text(Metrics.Render(sampler), "text/plain; version=0.0.4; charset=utf-8"));

app.Run();
return 0;

static class Metrics
{
    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
    static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Render(LinuxSampler sampler)
    {
        var sb = new StringBuilder();
        var s = sampler.Latest;
        void Gauge(string name, string help)
        {
            sb.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
            sb.Append("# TYPE ").Append(name).AppendLine(" gauge");
        }
        void PerGpu(string name, string help, string key, double scale = 1)
        {
            if (s == null) return;
            bool header = false;
            foreach (var g in s.Gpus)
            {
                if (!g.V.TryGetValue(key, out var v) || double.IsNaN(v)) continue;
                if (!header) { Gauge(name, help); header = true; }
                sb.Append(name).Append("{gpu=\"").Append(g.Gpu.Index).Append("\",name=\"").Append(Esc(g.Gpu.Name)).Append("\"} ").AppendLine(F(v * scale));
            }
        }
        PerGpu("gpumon_memory_used_bytes", "VRAM in use.", "mem.used", 1048576);
        PerGpu("gpumon_memory_total_bytes", "VRAM installed.", "mem.total", 1048576);
        PerGpu("gpumon_memory_reserved_bytes", "VRAM reserved by the driver.", "mem.reserved", 1048576);
        PerGpu("gpumon_utilization_gpu_percent", "GPU engine utilisation.", "load.gpu");
        PerGpu("gpumon_utilization_memory_percent", "Memory controller utilisation (time the memory bus was busy).", "load.mem");
        PerGpu("gpumon_utilization_encoder_percent", "Video encoder utilisation.", "load.enc");
        PerGpu("gpumon_utilization_decoder_percent", "Video decoder utilisation.", "load.dec");
        PerGpu("gpumon_temperature_celsius", "GPU temperature.", "temp.gpu");
        PerGpu("gpumon_memory_temperature_celsius", "Memory temperature.", "temp.mem");
        PerGpu("gpumon_fan_percent", "Fan speed.", "fan");
        PerGpu("gpumon_power_watts", "Board power draw.", "power");
        PerGpu("gpumon_power_limit_watts", "Enforced power limit.", "power.max");
        PerGpu("gpumon_clock_graphics_mhz", "Graphics clock.", "clk.gfx");
        PerGpu("gpumon_clock_memory_mhz", "Memory clock.", "clk.mem");
        PerGpu("gpumon_clock_video_mhz", "Video clock.", "clk.video");
        PerGpu("gpumon_pcie_rx_bytes_per_second", "PCIe traffic host to GPU.", "pcie.rx", 1048576);
        PerGpu("gpumon_pcie_tx_bytes_per_second", "PCIe traffic GPU to host.", "pcie.tx", 1048576);
        PerGpu("gpumon_pcie_link_generation", "Current PCIe generation.", "pcie.gen");
        PerGpu("gpumon_pcie_link_width", "Current PCIe lane count.", "pcie.width");
        PerGpu("gpumon_pcie_link_generation_max", "Highest PCIe generation the card supports.", "pcie.maxgen");
        PerGpu("gpumon_pcie_link_width_max", "Widest PCIe link the card supports.", "pcie.maxwidth");
        PerGpu("gpumon_pcie_link_capacity_bytes_per_second", "Capacity of the current PCIe link, per direction.", "pcie.cap", 1048576);
        PerGpu("gpumon_pcie_link_utilization_percent", "Busier PCIe direction as a share of the current link.", "pcie.util");
        PerGpu("gpumon_pcie_replays", "PCIe replays during the last sample (link errors).", "pcie.replay");

        if (s != null && s.Procs.Count > 0)
        {
            Gauge("gpumon_process_memory_used_bytes", "VRAM held by a process (0 when the driver does not report it).");
            foreach (var p in s.Procs)
                sb.Append("gpumon_process_memory_used_bytes{gpu=\"").Append(s.Gpus.First(g => g.Gpu.Luid == p.Luid).Gpu.Index)
                  .Append("\",pid=\"").Append(p.Pid).Append("\",name=\"").Append(Esc(p.Name)).Append("\",container=\"").Append(Esc(p.Container)).Append("\"} ")
                  .AppendLine(p.Dedicated.ToString(CultureInfo.InvariantCulture));
        }
        Gauge("gpumon_up", "1 when NVML is initialised and sampling.");
        sb.Append("gpumon_up ").AppendLine(s != null ? "1" : "0");
        return sb.ToString();
    }
}
