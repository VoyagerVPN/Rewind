using System.IO;

namespace Rewind.DiskBuffer;

public sealed record MediaSegment(string Path, TimeSpan Start, TimeSpan End);

public sealed class DiskReplayBuffer(string root, TimeSpan history, bool continuousTimeline = false)
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly List<RangeHold> _holds = [];
    private TimeSpan _latestEnd;

    public void RegisterClosedSegment(MediaSegment segment)
    {
        string path = System.IO.Path.GetFullPath(segment.Path);
        string relative = System.IO.Path.GetRelativePath(Root, path);
        if (relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            System.IO.Path.IsPathRooted(relative) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Segment must be a regular file inside the buffer root.", nameof(segment));
        lock (_gate)
        {
            // В непрерывном VFR-потоке последний кадр держится до начала следующего файла.
            // Отсутствующие последовательности файлов проверяет владелец манифеста.
            if (continuousTimeline && _entries.Count > 0 && _entries[^1].Segment.End < segment.Start)
                _entries[^1].Segment = _entries[^1].Segment with { End = segment.Start };
            _entries.Add(new Entry(segment with { Path = path }));
            _latestEnd = segment.End > _latestEnd ? segment.End : _latestEnd;
            Prune();
        }
    }

    public ReplayLease Reserve(TimeSpan end, TimeSpan duration)
    {
        lock (_gate)
        {
            if (duration <= TimeSpan.Zero || end > _latestEnd ||
                end - duration < _latestEnd - History && !_holds.Any(hold => hold.Start <= end - duration && end <= hold.End))
                throw new InvalidOperationException("Requested history is not available.");
            Entry[] selected = _entries.Where(entry => entry.Segment.Start < end && entry.Segment.End > end - duration)
                .OrderBy(entry => entry.Segment.Start).ToArray();
            if (selected.Length == 0 || selected[0].Segment.Start > end - duration || selected[^1].Segment.End < end)
                throw new InvalidOperationException("Requested history is not available.");
            for (int index = 1; index < selected.Length; index++)
                if (selected[index].Segment.Start - selected[index - 1].Segment.End > TimeSpan.FromMilliseconds(1))
                    throw new InvalidOperationException("Requested history contains a gap.");
            foreach (Entry entry in selected)
                entry.Leases++;
            return new ReplayLease(selected.Select(entry => entry.Segment).ToArray(), () => Release(selected));
        }
    }

    public IDisposable HoldRange(TimeSpan start, TimeSpan end)
    {
        if (start >= end) throw new ArgumentException("The held replay range must be positive.");
        lock (_gate)
        {
            var hold = new RangeHold(this, start, end);
            _holds.Add(hold);
            return hold;
        }
    }

    private void Release(IReadOnlyList<Entry> entries)
    {
        lock (_gate)
        {
            foreach (Entry entry in entries)
                entry.Leases--;
            Prune();
        }
    }

    private void Prune()
    {
        // Сохранение удерживает нужные фрагменты, даже если обычная история уже сдвинулась.
        foreach (Entry entry in _entries.Where(entry => entry.Leases == 0 && entry.Segment.End <= _latestEnd - History &&
            !_holds.Any(hold => entry.Segment.Start < hold.End && entry.Segment.End >= hold.Start)).ToArray())
        {
            try
            {
                File.Delete(entry.Segment.Path);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                // Чужой читатель может ненадолго блокировать удаление; повторим на следующем обороте.
                continue;
            }
            _entries.Remove(entry);
        }
    }

    private sealed class RangeHold(DiskReplayBuffer buffer, TimeSpan start, TimeSpan end) : IDisposable
    {
        public TimeSpan Start => start;
        public TimeSpan End => end;

        public void Dispose()
        {
            lock (buffer._gate)
            {
                if (buffer._holds.Remove(this)) buffer.Prune();
            }
        }
    }

    private sealed class Entry(MediaSegment segment)
    {
        public MediaSegment Segment { get; set; } = segment;
        public int Leases { get; set; }
    }

    public string Root { get; } = System.IO.Path.GetFullPath(root);
    public TimeSpan History { get; } = history;
}

public sealed class ReplayLease(IReadOnlyList<MediaSegment> segments, Action release) : IDisposable
{
    private Action? _release = release;
    public IReadOnlyList<MediaSegment> Segments { get; } = segments;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
