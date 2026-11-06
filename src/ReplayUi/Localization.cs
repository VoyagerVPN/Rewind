using System.Collections;
using System.Globalization;
using System.Resources;

namespace Rewind.Preview;

public static class Localization
{
    private static readonly ResourceManager Resources = new("Rewind.Preview.Languages.Strings", typeof(Localization).Assembly);
    private static volatile string _language = "en";

    public static string Language => _language;

    public static void SetLanguage(string language)
    {
        if (language is not ("en" or "ru")) throw new ArgumentOutOfRangeException(nameof(language));
        _language = language;
    }

    public static string Text(string key) => Resources.GetString(key, CultureInfo.GetCultureInfo(Language))
        ?? throw new MissingManifestResourceException($"Missing localized resource: {key}");

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "ru" ? "ru-RU" : "en-US");

    public static string Format(string key, params object?[] arguments) => string.Format(Culture, Text(key), arguments);

    public static string Count(string kind, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        string form = Language == "en" ? (count == 1 ? "One" : "Many") :
            count % 100 is >= 11 and <= 14 ? "Many" : (count % 10) switch { 1 => "One", 2 or 3 or 4 => "Few", _ => "Many" };
        return Format($"Count.{kind}.{form}", count);
    }

    public static IEnumerable<KeyValuePair<string, string>> All()
    {
        ResourceSet neutral = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true)
            ?? throw new MissingManifestResourceException("Missing neutral localization resources.");
        foreach (DictionaryEntry entry in neutral)
            if (entry.Key is string key) yield return new(key, Text(key));
    }
}
