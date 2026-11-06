using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Rewind.Preview;

public static class AppVersion
{
    public static string InformationalVersion { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static string Display => InformationalVersion.Split('+')[0];
    public static string BinaryPath => Environment.ProcessPath ?? "unknown";
    public static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "capture", "Rewind.CaptureWorker.exe");
    public static string? WorkerVersion => File.Exists(WorkerPath) ? FileVersionInfo.GetVersionInfo(WorkerPath).ProductVersion : null;
    public static string Distribution => File.Exists(Path.Combine(AppContext.BaseDirectory, "install-manifest.json")) ? "Установленная" : "Локальная";
    public static string Details => Localization.Format("Version.Details", Localization.Text(Distribution == "Установленная" ? "Version.Installed" : "Version.Local"), InformationalVersion, BinaryPath, WorkerVersion ?? Localization.Text("Version.Missing"));
    public static object RuntimeInfo() => new { version = InformationalVersion, binaryPath = BinaryPath,
        workerPath = WorkerPath, workerVersion = WorkerVersion, distribution = Distribution };
}
