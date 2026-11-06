using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Rewind.Preview;

internal static class UiTelemetryCheck
{
    public static async Task RunAsync(string diagnostics)
    {
        string run = Path.Combine(diagnostics, "ui-telemetry-" + Guid.NewGuid().ToString("N"));
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        using var telemetry = new UiTelemetry(dispatcher, run, () => true);
        await Task.Delay(200);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = Stopwatch.StartNew();
        void InputWork()
        {
            // Поток обслуживает ввод, но фоновая очередь временно не получает времени.
            Thread.Sleep(2);
            if (busy.ElapsedMilliseconds < 400) _ = dispatcher.BeginInvoke(DispatcherPriority.Input, InputWork);
            else finished.TrySetResult();
        }
        _ = dispatcher.BeginInvoke(DispatcherPriority.Input, InputWork);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        string snapshot = await telemetry.SnapshotAsync();
        using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(snapshot));
        if (!report.RootElement.TryGetProperty("inputDelays", out JsonElement input))
            throw new InvalidOperationException("Диагностика не различает ожидание ввода и фоновой очереди.");
        double[] background = report.RootElement.GetProperty("delays").EnumerateArray().Select(x => x.GetProperty("milliseconds").GetDouble()).ToArray();
        double[] inputs = input.EnumerateArray().Select(x => x.GetProperty("milliseconds").GetDouble()).ToArray();
        if (background.DefaultIfEmpty().Max() < 250 || inputs.Length < 3 || inputs.Max() >= 100)
            throw new InvalidOperationException($"Нужно различить голодание Background и обслуживаемый Input: background={background.DefaultIfEmpty().Max():F1}, input={inputs.DefaultIfEmpty().Max():F1}, count={inputs.Length}.");
        double operationMs = CheckSlowOperation(diagnostics, dispatcher);
        await File.WriteAllTextAsync(Path.Combine(diagnostics, "ui-telemetry-check.json"), JsonSerializer.Serialize(new
        {
            passed = true, run, snapshot, backgroundMaximumMs = background.Max(), inputMaximumMs = inputs.Max(), inputSamples = inputs.Length, operationMs,
            measurement = "Controlled dispatcher priority starvation on an own WPF dispatcher; not physical input-to-photon latency"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double CheckSlowOperation(string diagnostics, Dispatcher dispatcher)
    {
        string run = Path.Combine(diagnostics, "ui-operation-" + Guid.NewGuid().ToString("N"));
        using (var telemetry = new UiTelemetry(dispatcher, run, () => true))
        {
            try
            {
                using var timing = telemetry.Measure("operation-with-error");
                Thread.Sleep(70);
                throw new InvalidOperationException("expected-check-error");
            }
            catch (InvalidOperationException error) when (error.Message == "expected-check-error") { }
        }
        using JsonDocument events = JsonDocument.Parse("[" + string.Join(",", File.ReadAllLines(Directory.GetFiles(run, "session-*.jsonl").Single())) + "]");
        JsonElement[] operations = events.RootElement.EnumerateArray().Where(x => x.GetProperty("name").GetString() == "ui-operation").ToArray();
        if (operations.Length != 1) throw new InvalidOperationException("Долгая операция с исключением должна оставлять один замер своего участка.");
        JsonElement data = operations[0].GetProperty("data");
        double elapsed = data.GetProperty("milliseconds").GetDouble();
        if (data.GetProperty("stage").GetString() != "operation-with-error" || elapsed < 50 || !data.GetProperty("visible").GetBoolean())
            throw new InvalidOperationException("Замер должен сохранять участок, длительность и видимость даже при исключении.");
        return elapsed;
    }
}
