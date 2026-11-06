using System.Globalization;

namespace Rewind.Preview;

public sealed class ReplayIntervals
{
    private readonly SortedSet<int> _markers;
    public int MaximumSeconds { get; private set; }
    public int PreferredDefaultSeconds { get; private set; }
    public int DefaultSeconds => PreferredDefaultSeconds <= MaximumSeconds ? PreferredDefaultSeconds : ActiveMarkers.LastOrDefault(MaximumSeconds);
    public IReadOnlyList<int> Markers => _markers.ToArray();
    public IReadOnlyList<int> ActiveMarkers => _markers.Where(value => value <= MaximumSeconds).ToArray();

    public ReplayIntervals(int maximumSeconds, IEnumerable<int> markers, int defaultSeconds)
    {
        if (maximumSeconds is < 4 or > 900) throw new ArgumentException("История должна быть от 0:04 до 15:00.");
        MaximumSeconds = maximumSeconds;
        _markers = new SortedSet<int>(markers);
        if (_markers.Any(value => value < 1 || value > 900) || defaultSeconds is < 1 or > 900) throw new ArgumentException("Отметка должна быть от 0:01 до 15:00.");
        _markers.Add(defaultSeconds);
        PreferredDefaultSeconds = defaultSeconds;
    }

    public void SetDefault(int seconds)
    {
        if (!_markers.Contains(seconds) || seconds < 1 || seconds > MaximumSeconds) throw new ArgumentException("Отметка не найдена на шкале.");
        PreferredDefaultSeconds = seconds;
    }

    public void Add(int seconds)
    {
        if (seconds < 1 || seconds > MaximumSeconds) throw new ArgumentException("Отметка должна помещаться в историю.");
        _markers.Add(seconds);
    }

    public void Resize(int maximumSeconds)
    {
        if (maximumSeconds is < 4 or > 900) throw new ArgumentException("История должна быть от 0:04 до 15:00.");
        MaximumSeconds = maximumSeconds;
    }

    public void Remove(int seconds)
    {
        if (_markers.Count == 1) throw new ArgumentException("Оставь хотя бы одну отметку.");
        _markers.Remove(seconds);
        if (!_markers.Contains(PreferredDefaultSeconds)) PreferredDefaultSeconds = ActiveMarkers.LastOrDefault(_markers.Max);
    }

    public void Move(int fromSeconds, int toSeconds)
    {
        if (!_markers.Contains(fromSeconds)) throw new ArgumentException("Отметка не найдена.");
        if (fromSeconds == toSeconds) return;
        if (toSeconds < 1 || toSeconds > MaximumSeconds) throw new ArgumentException("Отметка должна помещаться в историю.");
        if (_markers.Contains(toSeconds)) throw new ArgumentException("Такая отметка уже есть.");
        _markers.Remove(fromSeconds);
        _markers.Add(toSeconds);
        if (PreferredDefaultSeconds == fromSeconds) PreferredDefaultSeconds = toSeconds;
    }

    public static int Snap(double seconds, int maximumSeconds) =>
        Math.Clamp((int)Math.Round(seconds / 15, MidpointRounding.AwayFromZero) * 15, Math.Min(15, maximumSeconds), maximumSeconds);

    public static string Format(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    public static int Parse(string text)
    {
        string[] parts = text.Trim().Split(':');
        if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return value;
        if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) && seconds < 60 && minutes <= 15)
            return checked(minutes * 60 + seconds);
        throw new ArgumentException("Введи время в формате м:сс, например 1:30.");
    }
}

internal sealed class ReplayDurationSelection
{
    private int _index;
    private bool _closed;
    public IReadOnlyList<int> Markers { get; }
    public int Index => _index;
    public int Seconds => Markers[_index];

    public ReplayDurationSelection(ReplayIntervals intervals)
    {
        int[] markers = intervals.ActiveMarkers.Append(intervals.DefaultSeconds).Distinct().Order().ToArray();
        Markers = markers;
        _index = Array.IndexOf(markers, intervals.DefaultSeconds);
    }

    public bool Step(int direction)
    {
        int next = Math.Clamp(_index + direction, 0, Markers.Count - 1);
        if (next == _index) return false;
        _index = next;
        return true;
    }

    public void Cancel() => _closed = true;

    public int? Complete()
    {
        if (_closed) return null;
        _closed = true;
        return Seconds;
    }
}
