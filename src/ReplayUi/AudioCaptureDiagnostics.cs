namespace Rewind.Preview;

public sealed record AudioSourceStatus(string Source, string Device, double Peak, string? Warning,
    bool Connected = false, long Packets = 0, long SignalPackets = 0, AudioBufferMetrics? Buffer = null,
    long Discontinuities = 0, long Reconnects = 0, AudioInputTiming? InputTiming = null);

public sealed record AudioInputTiming(double PacketAgeMilliseconds, double PacketAgeMaximumMilliseconds,
    double ClockOffsetMilliseconds, long EmptyCallbacks, long TimestampFallbacks, long ClockCorrections = 0);

public sealed record AudioTransportMetrics(long WrittenFrames, long Writes, double PendingWriteMilliseconds,
    double PipeWaitMaximumMilliseconds, long PipeWaitEvents, double WakeDelayMaximumMilliseconds, long WakeDelayEvents);

public sealed record AudioCaptureDiagnostics(AudioTransportMetrics Transport, AudioSourceStatus[] Sources)
{
    public static string SourceName(string source) => source switch
    {
        "system" => "Система", "discord" => "Discord", "microphone" => "Микрофон", _ => source
    };

    public string Describe()
    {
        string transport = $"Передача звука кодировщику: сейчас {Transport.PendingWriteMilliseconds:F0} мс · максимум {Transport.PipeWaitMaximumMilliseconds:F0} мс\n" +
            $"Опоздание отправителя: максимум {Transport.WakeDelayMaximumMilliseconds:F0} мс\nМаксимумы и потери — за текущий сеанс.";
        return transport + string.Concat(Sources.Select(source => source.Buffer is not AudioBufferMetrics buffer ? "" :
            $"\n{SourceName(source.Source)}: очередь {buffer.BufferedMilliseconds:F0} / {buffer.CapacityMilliseconds:F0} мс · пик {buffer.PeakBufferedMilliseconds:F0} мс" +
            $" · потери {buffer.DroppedMilliseconds:F0} мс · переподключения {source.Reconnects}" +
            (source.InputTiming is AudioInputTiming input ? $"\n  Доставка пакета: максимум {input.PacketAgeMaximumMilliseconds:F0} мс · коррекции часов {input.ClockCorrections}" : ""))) +
            "\nОчередь — накопленный звук. Пропуски сохраняют место на временной шкале; следующие пакеты не сдвигаются.";
    }
}
