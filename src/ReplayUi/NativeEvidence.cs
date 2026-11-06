using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Rewind.Preview;

public static class NativeEvidence
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    public static async Task WriteAsync(string directory, int renderingTier, int width, int height)
    {
        await WriteGate.WaitAsync();
        try
        {
        object report = await Task.Run(() =>
        {
            using Process process = Process.GetCurrentProcess();
            string[] modules = process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName).Order().ToArray();
            return new { utc = DateTime.UtcNow, processId = Environment.ProcessId,
                ui = "WPF", playback = "isolated LibVLC worker in child HWND; separate FFmpeg/WASAPI audio",
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), wpfRenderingTier = renderingTier,
                videoWidth = width, videoHeight = height, modules,
                limitations = new[] { "WPF rendering tier is a capability class, not proof of hardware video decoding.",
                    "Module presence does not measure frame latency, synchronization or gaming performance." } };
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "native-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { WriteGate.Release(); }
    }
}
