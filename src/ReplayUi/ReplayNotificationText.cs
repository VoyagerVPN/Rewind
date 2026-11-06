using System.Globalization;
using System.IO;

namespace Rewind.Preview;

public static class ReplayNotificationText
{
    public static string SavedDetail(int seconds, long totalBytes, IReadOnlyList<string> files)
    {
        string parameters = string.Create(CultureInfo.GetCultureInfo("ru-RU"),
            $"{ReplayIntervals.Format(seconds)} · {totalBytes / 1048576d:F1} МБ · {ScreenCount(files.Count)}");
        return parameters + "\n" + string.Join("\n", files.Select(Path.GetFileName));
    }

    public static string ScreenCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        // Окончания 11–14 имеют приоритет над последней цифрой.
        string noun = count % 100 is >= 11 and <= 14 ? "экранов" : (count % 10) switch
        {
            1 => "экран",
            2 or 3 or 4 => "экрана",
            _ => "экранов"
        };
        return $"{count} {noun}";
    }
}
