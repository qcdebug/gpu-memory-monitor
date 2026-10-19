using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace GpuMon;

/// <summary>Writes a plain-text report to Documents\GpuMon Logs so testers have something to send back.</summary>
public static class CrashLog
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
    public static Func<string>? Context;   // set by the app: GPU list, NVML status
    static int _written;

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, "UI thread", true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "background thread", false);
        TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception, "task", false); e.SetObserved(); };
    }

    /// <summary>Logs a handled exception without any dialog (used where the app keeps running).</summary>
    public static void Note(Exception ex, string where) { if (_written < 20) Write(ex, where); }

    public static void Report(Exception ex, string where, bool offerContinue)
    {
        string? path = Write(ex, where);
        string msg = $"GpuMon hit an unexpected error ({where}).\r\n\r\n{ex.GetType().Name}: {ex.Message}\r\n\r\n" +
                     (path != null ? $"A report was saved to:\r\n{path}\r\n\r\nPlease send that file to whoever gave you the program." : "");
        try { MessageBox.Show(msg, "GpuMon", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        if (!offerContinue) Environment.Exit(1);
    }

    static string? Write(Exception ex, string where)
    {
        try
        {
            _written++;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GpuMon Logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"gpumon_crash_{DateTime.Now:yyyyMMdd_HHmmss}_{_written}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"GpuMon {Version}   {DateTime.Now:yyyy-MM-dd HH:mm:ss}   where: {where}");
            sb.AppendLine($"OS: {Environment.OSVersion}   64-bit OS: {Environment.Is64BitOperatingSystem}   .NET {Environment.Version}");
            try { sb.AppendLine(Context?.Invoke() ?? ""); } catch { }
            sb.AppendLine().AppendLine(ex.ToString());
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch { return null; }
    }
}
