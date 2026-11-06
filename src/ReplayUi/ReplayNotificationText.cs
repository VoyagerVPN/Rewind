using System.Globalization;
using System.IO;

namespace Rewind.Preview;

public static class ReplayNotificationText
{
    public static string SavedDetail(int seconds, long totalBytes, IReadOnlyList<string> files)
    {
        string parameters = Localization.Format("Saved.Detail", ReplayIntervals.Format(seconds), totalBytes / 1048576d, ScreenCount(files.Count));
        return parameters + "\n" + string.Join("\n", files.Select(Path.GetFileName));
    }

    public static string ScreenCount(int count)
    {
        return Localization.Count("Screen", count);
    }
}
