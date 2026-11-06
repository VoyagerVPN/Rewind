using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Rewind.Preview;

public sealed class LanguagePreferencesFile(string path)
{
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    public string Load(string? systemLanguage = null)
    {
        if (File.Exists(path))
        {
            string? saved = JsonSerializer.Deserialize<LanguagePreference>(File.ReadAllText(path))?.Language;
            if (saved is "en" or "ru") return saved;
        }
        string name = systemLanguage ?? SystemUiCulture.Name;
        return name.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("ru-", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
    }

    public void Save(string language)
    {
        if (language is not ("en" or "ru")) throw new ArgumentOutOfRangeException(nameof(language));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new LanguagePreference(language)));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void SetInitialLanguage(string language)
    {
        if (language is not ("en" or "ru")) throw new ArgumentOutOfRangeException(nameof(language));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, new LanguagePreference(language));
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record LanguagePreference(string Language);
}
