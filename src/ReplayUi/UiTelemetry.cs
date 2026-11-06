using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;

namespace Rewind.Preview;

public sealed class UiTelemetry : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _visible;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly List<double> _openings = [];
    private sealed class ProbeLane(DispatcherPriority priority, string eventName)
    {
        public readonly DispatcherPriority Priority = priority;
        public readonly string EventName = eventName;
        public readonly Queue<(double Delay, bool Visible)> Samples = new();
        public int Pending;
    }
    private readonly ProbeLane _background = new(DispatcherPriority.Background, "ui-delay");
    private readonly ProbeLane _input = new(DispatcherPriority.Input, "ui-input-delay");
    private readonly Timer _timer;
    private readonly Channel<string> _events = Channel.CreateBounded<string>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _writer;
    private bool _disposed;

    public UiTelemetry(Dispatcher dispatcher, string directory, Func<bool> visible)
    {
        _dispatcher = dispatcher;
        _directory = directory;
        _visible = visible;
        Directory.CreateDirectory(directory);
        string events = Path.Combine(directory, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
        _writer = Task.Run(async () =>
        {
            await using var output = new StreamWriter(new FileStream(events, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384, useAsync: true));
            await foreach (string line in _events.Reader.ReadAllAsync()) { await output.WriteLineAsync(line); await output.FlushAsync(); }
        });
        _timer = new Timer(_ => Probe(), null, 0, 100);
        Event("session", new { processId = Environment.ProcessId, processorCount = Environment.ProcessorCount,
            build = AppVersion.RuntimeInfo(), arguments = Environment.GetCommandLineArgs() });
    }

    private void Probe()
    {
        if (_disposed) return;
        QueueProbe(_background);
        QueueProbe(_input);
    }

    private void QueueProbe(ProbeLane lane)
    {
        // Голодание фоновой очереди не должно подавлять независимую проверку ввода.
        if (Interlocked.Exchange(ref lane.Pending, 1) != 0) return;
        long start = Stopwatch.GetTimestamp();
        _dispatcher.BeginInvoke(lane.Priority, () =>
        {
            double delay = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            bool visible = _visible();
            lock (_gate)
            {
                lane.Samples.Enqueue((delay, visible));
                while (lane.Samples.Count > 2048) lane.Samples.Dequeue();
            }
            Interlocked.Exchange(ref lane.Pending, 0);
            if (delay > 30) Event(lane.EventName, new { milliseconds = delay, visible, priority = lane.Priority.ToString() });
        });
    }

    public void Opening(double milliseconds)
    {
        lock (_gate) { _openings.Add(milliseconds); if (_openings.Count > 256) _openings.RemoveAt(0); }
        Event("opening-frame-submitted", new { milliseconds });
    }
    public void Event(string name, object data) => _events.Writer.TryWrite(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, name, data }));
    private sealed class OperationTiming(UiTelemetry owner, string stage) : IDisposable
    {
        private UiTelemetry? _owner = owner;
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            UiTelemetry? current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            double milliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            if (milliseconds > 30)
                current.Event("ui-operation", new { stage, milliseconds, visible = current._visible() });
        }
    }
    // Область замера используется на UI-потоке и завершается также при исключении.
    public IDisposable Measure(string stage) => new OperationTiming(this, stage);
    public string Summary()
    {
        lock (_gate)
        {
            double[] visible = _background.Samples.Where(item => item.Visible).Select(item => item.Delay).ToArray();
            return $"Открытие → кадр WPF: {Percentile(_openings, 0.5):F1} мс (медиана, {_openings.Count})\n" +
                $"Фоновая очередь UI: p95 {Percentile(visible, 0.95):F1} мс · max {visible.DefaultIfEmpty().Max():F1} мс\n" +
                "Это отправка кадра, а не время появления на мониторе.";
        }
    }
    public async Task<string> SnapshotAsync()
    {
        object snapshot;
        lock (_gate)
        {
            using Process process = Process.GetCurrentProcess();
            snapshot = new { utc = DateTime.UtcNow, processId = Environment.ProcessId, workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                openingsMilliseconds = _openings.ToArray(),
                delays = _background.Samples.Select(item => new { milliseconds = item.Delay, visible = item.Visible }).ToArray(),
                inputDelays = _input.Samples.Select(item => new { milliseconds = item.Delay, visible = item.Visible }).ToArray() };
        }
        string path = Path.Combine(_directory, $"metrics-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        Event("snapshot", new { file = Path.GetFileName(path) });
        return path;
    }
    private static double Percentile(IEnumerable<double> samples, double percentile)
    {
        double[] ordered = samples.Order().ToArray();
        return ordered.Length == 0 ? 0 : ordered[(int)Math.Ceiling((ordered.Length - 1) * percentile)];
    }
    public void Dispose()
    {
        _disposed = true; _timer.Dispose(); _events.Writer.TryComplete(); _writer.GetAwaiter().GetResult();
    }
}
