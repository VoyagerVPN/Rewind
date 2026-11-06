namespace Rewind.Preview;

internal sealed record ReplaySessionGroup(string Application, DateTime StartedUtc, DateTime LastSavedUtc, bool Estimated, LibraryEntry[] Entries);

internal static class ReplaySessions
{
    public static ReplaySessionGroup[] Group(IEnumerable<LibraryEntry> entries)
    {
        var sessions = new List<ReplaySessionGroup>();
        foreach (var application in entries.GroupBy(entry => entry.Game, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var run in application.Where(entry => !string.IsNullOrEmpty(entry.Clip.SessionId)).GroupBy(entry => entry.Clip.SessionId, StringComparer.Ordinal))
                sessions.Add(Create(application.Key, run.ToArray(), false));
            var estimated = new List<LibraryEntry>();
            foreach (LibraryEntry entry in application.Where(entry => string.IsNullOrEmpty(entry.Clip.SessionId)).OrderBy(entry => entry.CreatedUtc))
            {
                if (estimated.Count > 0 && entry.CreatedUtc - estimated[^1].CreatedUtc > TimeSpan.FromMinutes(90))
                { sessions.Add(Create(application.Key, estimated.ToArray(), true)); estimated.Clear(); }
                estimated.Add(entry);
            }
            if (estimated.Count > 0) sessions.Add(Create(application.Key, estimated.ToArray(), true));
        }
        return sessions.OrderByDescending(session => session.LastSavedUtc).ThenBy(session => session.Application, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ReplaySessionGroup Create(string application, LibraryEntry[] entries, bool estimated) => new(application,
        entries.Min(entry => entry.Clip.SessionStartedUtc?.ToUniversalTime() ?? entry.CreatedUtc), entries.Max(entry => entry.CreatedUtc),
        estimated, entries.OrderByDescending(entry => entry.CreatedUtc).ToArray());
}
