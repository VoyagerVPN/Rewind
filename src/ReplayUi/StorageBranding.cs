using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rewind.Preview;

internal static class StorageBranding
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, out nint large, nint small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);

    public static string? FindExactShortcut(string name, IEnumerable<string> shortcuts) =>
        shortcuts.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase));

    public static Dictionary<string, (BitmapSource Icon, Color Color)> Load(IEnumerable<string> names, IEnumerable<string>? folders = null)
    {
        var paths = new List<string>();
        foreach (string folder in (folders ?? new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            try { paths.AddRange(Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(path => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        var result = new Dictionary<string, (BitmapSource, Color)>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? path = FindExactShortcut(name, paths);
            if (path is null) continue;
            nint handle = 0;
            if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                string? iconFile = null; int iconIndex = 0;
                try
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        int separator = line.IndexOf('='); if (separator < 0) continue;
                        string key = line[..separator].Trim(), value = line[(separator + 1)..].Trim();
                        if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) iconFile = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                        else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out iconIndex);
                    }
                    if (string.IsNullOrWhiteSpace(iconFile) || !File.Exists(iconFile) || ExtractIconEx(iconFile, iconIndex, out handle, 0, 1) == 0) continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            }
            else
            {
                if (SHGetFileInfo(path, 0, out ShellFileInfo info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) == 0) continue;
                handle = info.Icon;
            }
            if (handle == 0) continue;
            try
            {
                BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
                icon.Freeze(); result[name] = (icon, DominantColor(icon));
            }
            finally { DestroyIcon(handle); }
        }
        return result;
    }

    public static Color DominantColor(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        long red = 0, green = 0, blue = 0, weight = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            red += pixels[index + 2] * alpha; green += pixels[index + 1] * alpha; blue += pixels[index] * alpha; weight += alpha;
        }
        return weight == 0 ? Color.FromRgb(139, 165, 181) : Color.FromRgb((byte)(red / weight), (byte)(green / weight), (byte)(blue / weight));
    }
}
