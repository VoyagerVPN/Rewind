using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rewind.Preview;

internal sealed class TrayIndicator : IDisposable
{
    public const int CallbackMessage = 0x8001;
    private NotificationData _data;
    private readonly byte[] _logoPixels = new byte[32 * 32 * 4];
    private Color? _color;
    private bool _added;

    public TrayIndicator(nint window)
    {
        _data = new NotificationData { Size = Marshal.SizeOf<NotificationData>(), Window = window,
            Id = 42, Flags = 1 | 2 | 4, Callback = CallbackMessage, Tip = "Rewind", Info = "", InfoTitle = "" };
        var logo = new BitmapImage();
        logo.BeginInit(); logo.UriSource = new Uri("pack://application:,,,/Assets/Rewind.png"); logo.DecodePixelWidth = 32; logo.CacheOption = BitmapCacheOption.OnLoad; logo.EndInit();
        new FormatConvertedBitmap(logo, PixelFormats.Bgra32, null, 0).CopyPixels(_logoPixels, 32 * 4, 0);
        SetStatus(Color.FromRgb(196, 123, 116), "Rewind · Остановлен");
    }

    public void SetStatus(Color? color, string tip)
    {
        if (_added && _color == color && _data.Tip == tip) return;
        nint old = _data.Icon;
        byte[] mask = Enumerable.Repeat((byte)255, 128).ToArray();
        byte[] pixels = (byte[])_logoPixels.Clone();
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                int offset = (y * 32 + x) * 4;
                if (color is Color dot && (x - 26) * (x - 26) + (y - 26) * (y - 26) <= 5 * 5)
                { pixels[offset] = dot.B; pixels[offset + 1] = dot.G; pixels[offset + 2] = dot.R; pixels[offset + 3] = 255; }
                if (pixels[offset + 3] != 0) mask[y * 4 + x / 8] &= (byte)~(0x80 >> (x % 8));
            }
        nint icon = CreateIcon(0, 32, 32, 1, 32, mask, pixels);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        NotificationData updated = _data;
        updated.Icon = icon; updated.Tip = tip;
        if (!ShellNotifyIcon(_added ? 1u : 0u, ref updated))
        {
            DestroyIcon(icon);
            throw new Win32Exception("Не удалось добавить индикатор Rewind в область уведомлений.");
        }
        _data = updated; _added = true; _color = color;
        if (old != 0) DestroyIcon(old);
    }

    public void Dispose()
    {
        if (_added) ShellNotifyIcon(2, ref _data);
        if (_data.Icon != 0) DestroyIcon(_data.Icon);
        _data.Icon = 0; _added = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationData
    {
        public int Size;
        public nint Window;
        public uint Id, Flags;
        public int Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint operation, ref NotificationData data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIcon(nint instance, int width, int height, byte planes, byte bitsPerPixel, byte[] mask, byte[] pixels);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
