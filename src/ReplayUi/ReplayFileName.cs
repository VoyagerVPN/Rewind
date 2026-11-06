using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Rewind.Preview;

public static class ReplayFileName
{
    public const string DefaultFormat = "{app} {date:yyyy.MM.dd} - {time:HH.mm.ss.ff}.DVR";
    private static readonly Regex Tokens = new(@"\{(app|date|time|n|duration|screen|size)(?::([^{}]+))?\}", RegexOptions.CultureInvariant);
    public static string ApplicationFolder(string application)
    {
        string clean = string.Concat(application.Select(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character)).Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(clean)) clean = "Desktop";
        if (Regex.IsMatch(clean, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) clean = "_" + clean;
        return clean[..Math.Min(80, clean.Length)];
    }
    public static void Validate(string template) => _ = Format(template, "Desktop", new DateTime(2026, 10, 23, 14, 5, 9), 90, 1, "1");
    public static string Format(string template, string application, DateTime time, double seconds, int ordinal, string screen, long sizeBytes = 0)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 200) throw new ArgumentException("Формат имени должен содержать от 1 до 200 символов.");
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        string remaining = Tokens.Replace(template, "");
        if (remaining.IndexOfAny(['{', '}']) >= 0) throw new ArgumentException("Неизвестный плейсхолдер имени файла.");
        string name;
        try
        {
            name = Tokens.Replace(template, match => match.Groups[1].Value switch
            {
                "app" => ApplicationFolder(application), "screen" => ApplicationFolder(screen),
                "date" => time.ToString(match.Groups[2].Success ? match.Groups[2].Value : "yyyy.MM.dd", CultureInfo.InvariantCulture),
                "time" => time.ToString(match.Groups[2].Success ? match.Groups[2].Value : "HH.mm.ss.ff", CultureInfo.InvariantCulture),
                "n" => ordinal.ToString(match.Groups[2].Success ? match.Groups[2].Value : "000", CultureInfo.InvariantCulture),
                "size" => match.Groups[2].Success ? throw new ArgumentException("Для размера используется плейсхолдер {size} без дополнительного формата.") : FormatSize(sizeBytes),
                _ => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\-ss", CultureInfo.InvariantCulture)
            });
        }
        catch (FormatException) { throw new ArgumentException("Некорректный формат даты, времени или номера."); }
        if (name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || name.Any(char.IsControl) || name is "." or ".." || name.Trim().Length == 0 || name.EndsWith('.') || name.Length > 220)
            throw new ArgumentException("Имя содержит недопустимые символы или слишком длинное.");
        return name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? name : name + ".mp4";
    }
    private static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Reserve(string directory, string template, string application, DateTime time, double seconds, string screen, bool multiple, long sizeBytes = 0)
    {
        Directory.CreateDirectory(directory);
        int ordinal = template.Contains("{n", StringComparison.Ordinal) ? Directory.EnumerateFiles(directory, "*.mp4").Count(file => !file.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase)) + 1 : 1;
        string name = Format(template, application, time, seconds, ordinal, screen, sizeBytes);
        if (multiple && !template.Contains("{screen}", StringComparison.Ordinal)) name = Path.GetFileNameWithoutExtension(name) + " - " + screen + ".mp4";
        for (int collision = 0; collision < 10000; collision++)
        {
            string candidate = Path.Combine(directory, collision == 0 ? name : Path.GetFileNameWithoutExtension(name) + $" ({collision + 1}).mp4");
            if (File.Exists(candidate)) continue;
            try { using var reservation = new FileStream(Path.ChangeExtension(candidate, ".partial.mp4"), FileMode.CreateNew, FileAccess.Write, FileShare.None); return candidate; }
            catch (IOException) when (File.Exists(Path.ChangeExtension(candidate, ".partial.mp4"))) { }
        }
        throw new IOException("Не удалось подобрать свободное имя клипа.");
    }
}
