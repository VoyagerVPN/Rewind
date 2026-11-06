using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;

namespace Rewind.Preview;

internal static class UiLanguage
{
    private static ResourceDictionary? _resources;

    public static void Apply(string language)
    {
        if (language is not ("ru" or "en")) throw new ArgumentOutOfRangeException(nameof(language));
        Application app = Application.Current;
        Localization.SetLanguage(language);
        var next = new ResourceDictionary();
        foreach (var (key, value) in Localization.All()) next.Add(key, value);
        app.Resources.MergedDictionaries.Add(next);
        if (_resources is not null) app.Resources.MergedDictionaries.Remove(_resources);
        _resources = next;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
        // Язык задаётся только потоку приложения; системный язык Windows не меняется.
        if (!SetThreadPreferredUILanguages(0x8, (language == "ru" ? "ru-RU" : "en-US") + "\0", out _))
            System.Diagnostics.Trace.TraceWarning("Could not set the UI thread language: {0}", Marshal.GetLastWin32Error());
    }

    public static string Text(string key) => Localization.Text(key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPreferredUILanguages(uint flags, string languages, out uint count);
}
