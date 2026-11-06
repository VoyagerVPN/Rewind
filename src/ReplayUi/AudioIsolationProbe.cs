using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Rewind.Preview;

internal static class AudioIsolationProbe
{
    public static async Task RunAsync(string folder)
    {
        var sessions = SnapshotSessions();
        var baseline = await RecordAsync((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree);
        string ready = Path.Combine(folder, "isolation-tone-" + Guid.NewGuid().ToString("N"));
        using var helper = CaptureProcesses.Start(Environment.ProcessPath!, ["--audio-tone", ready]);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(ready)) await Task.Delay(20, deadline.Token);
            var excluded = await RecordAsync((uint)helper.Id, ProcessLoopbackMode.ExcludeTargetProcessTree);
            var included = await RecordAsync((uint)helper.Id, ProcessLoopbackMode.IncludeTargetProcessTree);
            using var player = new WasapiPlayerBuilder().WithLatency(30).Build();
            player.Init(new AudioRoutingCheck.Tone(880)); player.Play();
            var withSystemTone = await RecordAsync((uint)helper.Id, ProcessLoopbackMode.ExcludeTargetProcessTree);
            player.Stop();
            await File.WriteAllTextAsync(Path.Combine(folder, "audio-isolation-probe.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, output = AudioDeviceCatalog.DefaultOutput(), helperPid = helper.Id,
                baseline, excluded, included, withSystemTone, sessions, boundary = "Direct WASAPI bytes before buffers, FFmpeg, AAC and replay player" }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await File.WriteAllTextAsync(ready + ".stop", "stop");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await helper.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
            File.Delete(ready); File.Delete(ready + ".stop");
        }
    }

    private sealed record SessionSnapshot(string Device, uint Pid, string Process, string State, float Peak, bool Muted, float Volume);
    private static SessionSnapshot[] SnapshotSessions()
    {
        var result = new List<SessionSnapshot>();
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (MMDevice device in devices)
        using (device)
        {
            using var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                uint pid = session.GetProcessID;
                string name = "System";
                if (pid != 0)
                    try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
                    catch (ArgumentException) { name = "Exited"; }
                result.Add(new SessionSnapshot(device.FriendlyName, pid, name, session.State.ToString(), session.AudioMeterInformation.MasterPeakValue,
                    session.SimpleAudioVolume.Mute, session.SimpleAudioVolume.Volume));
            }
        }
        return result.ToArray();
    }

    private static async Task<AudioRoutingCheck.Spectrum> RecordAsync(uint pid, ProcessLoopbackMode mode)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) throw new PlatformNotSupportedException();
        using var bytes = new MemoryStream();
        object gate = new();
        await using (var recorder = await new WasapiRecorderBuilder().WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
            .WithBufferLength(20).WithProcessLoopback(pid, mode).BuildAsync())
        {
            recorder.DataAvailable += (packet, flags, _, _) =>
            { lock (gate) { if (((int)flags & 2) == 0) bytes.Write(packet); else bytes.Write(new byte[packet.Length]); } };
            recorder.StartRecording(); await Task.Delay(1600);
        }
        float[] stereo = MemoryMarshal.Cast<byte, float>(bytes.ToArray()).ToArray();
        return AudioRoutingCheck.Analyze(stereo.Where((_, index) => index % 2 == 0).ToArray());
    }
}
