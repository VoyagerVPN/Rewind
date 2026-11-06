using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class PlayerAudioCheck
{
    public static async Task RunAsync(string folder)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(folder);
        using var telemetry = new UiTelemetry(Dispatcher.CurrentDispatcher, folder, () => true);
        using var playerOutput = new ReplayAudioOutput();
        var player = new ReplayPlayerView(telemetry, folder, playerOutput, headless: true);
        telemetry.Event("player-native-runtime", new { mode = "isolated software video worker" });
        var host = new System.Windows.Window { Content = player, Width = 900, Height = 650, Left = 550, Top = 90,
            ShowActivated = false, WindowStyle = System.Windows.WindowStyle.None, Background = System.Windows.Media.Brushes.Black };
        await using var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync();
        using var packets = new MemoryStream();
        object gate = new();
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            lock (gate)
            {
                if (packets.Length > 2 * 1024 * 1024) return;
                if (((int)flags & 2) != 0) packets.Write(new byte[buffer.Length]); else packets.Write(buffer);
            }
        };
        async Task<AudioRoutingCheck.Spectrum> ListenAsync(bool late = false)
        {
            // Даём уже переданным в устройство пакетам закончиться перед измерением нового микса.
            await Task.Delay(250);
            lock (gate) packets.SetLength(0);
            await Task.Delay(850);
            byte[] bytes;
            lock (gate) bytes = packets.ToArray();
            float[] stereo = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            return AudioRoutingCheck.Analyze(stereo.Where((_, index) => index % 2 == 0).ToArray(), late ? 1320 : 660, late ? 1760 : 880);
        }
        try
        {
            recorder.StartRecording();
            string file = Path.Combine(folder, "player-timed-fixture.mp4");
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=0x1c1c1c:s=640x360:r=30:d=24",
                 "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=24",
                 "-f", "lavfi", "-i", "aevalsrc='0.08*sin(2*PI*if(lt(t,12),660,1320)*t)':s=48000:d=24",
                 "-f", "lavfi", "-i", "aevalsrc='0.08*sin(2*PI*if(lt(t,12),880,1760)*t)':s=48000:d=24",
                 "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-c:a", "aac", "-b:a", "192k", file], TimeSpan.FromSeconds(30));
            await player.LoadClipAsync(new Clip("Проверка трёх дорожек", "", file, "", [null, null, null], 24, 30,
                EmbeddedAudioSources: ["system", "discord", "microphone"]));
            await Task.Delay(1300);
            telemetry.Event("player-test-phase", new { phase = "listen-original" });
            var original = await ListenAsync();
            telemetry.Event("player-test-phase", new { phase = "original-spectrum", original });
            if (original.Tone660 < .001 || original.Tone880 < .001) throw new InvalidOperationException("Плеер не воспроизводит все дорожки: " + JsonSerializer.Serialize(original));
            player.SetAudioVolume(1, 0);
            var withoutDiscord = await ListenAsync();
            if (withoutDiscord.Tone660 > original.Tone660 * .05 || withoutDiscord.Tone880 < original.Tone880 * .8)
                throw new InvalidOperationException("Выключение Discord должно сохранять микрофон: " + JsonSerializer.Serialize(withoutDiscord));
            player.SetAudioVolume(2, 0);
            var withoutMicrophone = await ListenAsync();
            if (withoutMicrophone.Tone880 > original.Tone880 * .05 || withoutMicrophone.Peak < .001)
                throw new InvalidOperationException("Выключение микрофона должно сохранять системную дорожку: " + JsonSerializer.Serialize(withoutMicrophone));
            player.PausePlayback();
            player.SeekTo(4);
            player.TogglePlayback();
            await Task.Delay(400);
            if (player.PositionSeconds < 3.8 || Math.Abs(player.AudioPositionSeconds - player.PositionSeconds) > .15)
                throw new InvalidOperationException($"Пауза, перемотка и продолжение должны сохранять общую позицию видео и аудио: video={player.PositionSeconds:F3}, audio={player.AudioPositionSeconds:F3}, playing={player.IsPlaying}.");
            player.PausePlayback();
            var paused = await ListenAsync();
            if (paused.Peak > .0001) throw new InvalidOperationException("Пауза должна останавливать все дорожки.");
            player.SetAudioVolume(1, 1); player.SetAudioVolume(2, 1);
            player.SeekTo(17); player.TogglePlayback();
            await Task.Delay(650);
            var afterSeekForward = await ListenAsync(late: true);
            if (afterSeekForward.Tone660 < .005 || afterSeekForward.Tone880 < .005)
                throw new InvalidOperationException("После перемотки на 17 с должны звучать 1320/1760 Гц из второй половины, а не начало записи.");
            player.PausePlayback(); player.SeekTo(5); player.TogglePlayback();
            await Task.Delay(650);
            var afterSeekBackward = await ListenAsync();
            if (afterSeekBackward.Tone660 < .005 || afterSeekBackward.Tone880 < .005)
                throw new InvalidOperationException("После перемотки назад должны вернуться 660/880 Гц первой половины.");
            string alternate = Path.Combine(folder, "screen-switch-fixture.mp4");
            File.Copy(file, alternate, overwrite: true);
            var firstScreen = new Clip("Экран 1", "", file, "", [null, null, null], 24, 30,
                EmbeddedAudioSources: ["system", "discord", "microphone"], MonitorName: @"\\.\DISPLAY1");
            Clip secondScreen = firstScreen with { Video = alternate, MonitorName = @"\\.\DISPLAY2" };
            player.SetAudioVolume(1, 1); player.SetAudioVolume(2, 1);
            await player.LoadClipAsync(firstScreen with { Screens = [firstScreen, secondScreen] }, 17000);
            await Task.Delay(600);
            var screenPicker = (System.Windows.Controls.ComboBox)player.FindName("MonitorClipPicker");
            if (screenPicker.Items.Count != 2 || screenPicker.Visibility != System.Windows.Visibility.Visible)
                throw new InvalidOperationException("Плеер должен показывать переключатель экранов события.");
            screenPicker.SelectedIndex = 1;
            await Task.Delay(1100);
            var afterScreenSwitch = await ListenAsync(late: true);
            if (afterScreenSwitch.Tone660 < .005 || afterScreenSwitch.Tone880 < .005 || player.PositionSeconds < 17 || player.CurrentVideo != alternate)
                throw new InvalidOperationException("После переключения экрана должна сохраняться позиция и звук второй половины записи.");
            Task superseded = player.LoadClipAsync(firstScreen);
            Task latest = player.LoadClipAsync(secondScreen, 17000);
            await Task.WhenAll(superseded, latest);
            await Task.Delay(650);
            var afterRapidSwitch = await ListenAsync(late: true);
            if (player.CurrentVideo != alternate || afterRapidSwitch.Tone660 < .005 || afterRapidSwitch.Tone880 < .005)
                throw new InvalidOperationException("Быстрое переключение должно завершаться последним выбранным клипом и его звуком.");
            string chirp = Path.Combine(folder, "player-seek-chirp.mp4");
            await CaptureProcesses.RunAsync(CaptureProcesses.Ffmpeg,
                ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=s=320x180:r=30:d=12", "-f", "lavfi", "-i",
                 "aevalsrc=0.2*sin(2*PI*(300*t+50*t*t)):s=48000:d=12", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-b:a", "192k", chirp], TimeSpan.FromSeconds(20));
            await player.LoadClipAsync(new Clip("Проверка перемотки", "", chirp, "", [null, null, null], 12, 30, EmbeddedSystemAudio: true));
            await Task.Delay(250);
            player.SeekTo(3); await Task.Delay(25); player.SeekTo(9); await Task.Delay(25);
            player.SeekTo(7);
            DateTime seekDeadline = DateTime.UtcNow.AddSeconds(3);
            bool SeekReady() => ((System.Windows.Controls.TextBlock)player.FindName("PlayerStateText")).Text == "Воспроизведение" && player.AudioPositionSeconds >= 7 && Math.Abs(player.VideoPositionSeconds - player.AudioPositionSeconds) < .4;
            while (!SeekReady() && DateTime.UtcNow < seekDeadline) await Task.Delay(20);
            if (!SeekReady()) throw new InvalidOperationException("Последняя перемотка должна закончиться перед проверкой непрерывного воспроизведения.");
            // Во время «Перемотка…» ещё завершается старый вывод WASAPI; измеряем новый фрагмент после готовности плеера.
            lock (gate) packets.SetLength(0);
            await Task.Delay(1800);
            float[] captured;
            lock (gate) captured = MemoryMarshal.Cast<byte, float>(packets.ToArray()).ToArray();
            float[] mono = captured.Where((_, index) => index % 2 == 0).ToArray();
            var sweep = new List<double>();
            // Частота растёт на 100 Гц/с. Повтор уже прозвучавшего фрагмента даст падение частоты.
            for (int offset = 7200; offset + 3840 <= mono.Length; offset += 3840)
            {
                var crossings = new List<double>();
                for (int sample = offset + 1; sample < offset + 3840; sample++)
                    if (mono[sample - 1] < 0 && mono[sample] >= 0)
                        crossings.Add(sample - mono[sample] / (mono[sample] - mono[sample - 1]));
                if (crossings.Count > 20) sweep.Add((crossings.Count - 1) * 48000 / (crossings[^1] - crossings[0]));
            }
            if (sweep.Count < 10 || sweep[0] < 990 || sweep[0] > 1060 || sweep.Zip(sweep.Skip(1)).Any(pair => pair.Second < pair.First - 4))
                throw new InvalidOperationException("После серии перемоток звук должен непрерывно идти от последней позиции без повтора фрагмента: " + JsonSerializer.Serialize(sweep));
            player.PausePlayback();
            using var interruptedOutput = await playerOutput.AcquireAsync(new FailingSource(), CancellationToken.None);
            interruptedOutput.Play(); await Task.Delay(400);
            await player.LoadClipAsync(new Clip("Повтор после ошибки аудиовыхода", "", file, "", [null, null, null], 24, 30,
                EmbeddedAudioSources: ["system", "discord", "microphone"]));
            player.SetAudioVolume(1, 1); player.SetAudioVolume(2, 1);
            var afterReloadFailure = await ListenAsync();
            if (afterReloadFailure.Tone660 < .005 || afterReloadFailure.Tone880 < .005)
                throw new InvalidOperationException("Повторная загрузка в том же плеере должна восстановить отказавший аудиовыход.");
            player.PausePlayback();
            using var sharedOutput = new ReplayAudioOutput();
            await sharedOutput.WarmAsync();
            using var oldLease = await sharedOutput.AcquireAsync(new AudioRoutingCheck.Tone(660).ToSampleProvider(), CancellationToken.None);
            oldLease.Play();
            using var nextLease = await sharedOutput.AcquireAsync(new AudioRoutingCheck.Tone(880).ToSampleProvider(), CancellationToken.None);
            nextLease.Play();
            oldLease.Stop(); oldLease.Pause(); oldLease.Dispose();
            var afterOldCleanup = await ListenAsync();
            if (afterOldCleanup.Tone880 < .005 || afterOldCleanup.Tone660 > .001)
                throw new InvalidOperationException("Поздняя очистка старого плеера остановила новый звук.");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try
                {
                    using var unexpected = await sharedOutput.AcquireAsync(new AudioRoutingCheck.Tone(660).ToSampleProvider(), canceled.Token);
                    throw new InvalidOperationException("Отменённая загрузка получила аудиовыход.");
                }
                catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            }
            var afterCanceledLoad = await ListenAsync();
            if (afterCanceledLoad.Tone880 < .005) throw new InvalidOperationException("Отмена следующей загрузки остановила текущий звук.");
            using var faultyLease = await sharedOutput.AcquireAsync(new FailingSource(), CancellationToken.None);
            faultyLease.Play(); await Task.Delay(400);
            using var recoveredLease = await sharedOutput.AcquireAsync(new AudioRoutingCheck.Tone(660).ToSampleProvider(), CancellationToken.None);
            recoveredLease.Play();
            var afterOutputFailure = await ListenAsync();
            if (afterOutputFailure.Tone660 < .005) throw new InvalidOperationException("Аудиовыход не восстановился после ошибки чтения.");
            await File.WriteAllTextAsync(Path.Combine(folder, "player-audio.json"), JsonSerializer.Serialize(new
            { completedUtc = DateTime.UtcNow, original, withoutDiscord, withoutMicrophone, paused, afterSeekForward, afterSeekBackward, afterScreenSwitch, afterRapidSwitch, sweep,
                afterReloadFailure, afterOldCleanup, afterCanceledLoad, afterOutputFailure, lateFrequencies = new[] { 1320, 1760 },
                verified = "ReplayPlayerView: three actual WASAPI tracks, mixer, pause, time-coded sound after forward/backward seek, monitor switch retains position, latest rapid selection wins." },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { telemetry.Event("player-test-failure", new { error.Message }); throw; }
        finally { telemetry.Event("player-test-phase", new { phase = "closing" }); await player.ClosePlayerAsync().WaitAsync(TimeSpan.FromSeconds(10)); host.Close(); }
    }
    private sealed class FailingSource : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(Span<float> buffer) => throw new IOException("Тестовая ошибка аудиопотока.");
    }

}
