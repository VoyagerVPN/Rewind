using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rewind.Preview;

public partial class OverlayWindow
{
    private bool? _motionRecording;
    private Color? _motionStatusColor;
    private readonly SolidColorBrush _motionStatusBrush = new(Color.FromRgb(196, 123, 116));
    private double? _settingsIndicatorTarget;
    private byte[]? _nativePrintPixels;
    private PrintBitmapInfo _nativePrintInfo;
    private int _nativeOpeningVersion;

    private void QueueNativeOpening()
    {
        int version = ++_nativeOpeningVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { _ = FinishNativeOpeningAsync(version); });
    }

    private async Task FinishNativeOpeningAsync(int version)
    {
        try
        {
            // Loaded и фоновые задания карточек должны стартовать до фиксации кадра для AnimateWindow.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Task[] readyImages = _galleryWarmLoads.Where(task => !task.IsCompleted).ToArray();
            if (readyImages.Length > 0)
                await Task.WhenAny(Task.WhenAll(readyImages), Task.Delay(80));
            if (version != _nativeOpeningVersion || _closing || !IsVisible) return;
            ShowNativeOpening();
        }
        catch (Exception error)
        {
            _telemetry.Event("native-opening-error", new { error.Message });
            if (version == _nativeOpeningVersion && !_closing && IsVisible)
                Native.ShowWindow(new WindowInteropHelper(this).Handle, 8);
        }
        finally
        {
            if (version == _nativeOpeningVersion && !_closing) SetOpeningCloaked(false);
        }
    }

    private void CancelNativeOpening()
    {
        ++_nativeOpeningVersion;
        SetOpeningCloaked(false);
    }

    private void SetOpeningCloaked(bool value)
    {
        // Даём WPF подготовить первый кадр, не показывая целое окно перед системным выездом.
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        int cloak = value ? 1 : 0;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 13, ref cloak, sizeof(int)));
    }

    private void ShowNativeOpening()
    {
        using var timing = _telemetry.Measure("native-opening");
        if (!SystemParameters.ClientAreaAnimation) return;
        nint handle = new WindowInteropHelper(this).Handle;
        UpdateLayout();
        PrepareNativePrint();
        try
        {
            Native.ShowWindow(handle, 0);
            SetOpeningCloaked(false);
            // Windows выполняет единый выезд слева направо; AW_ACTIVATE намеренно отсутствует.
            bool shown = Native.AnimateWindow(handle, 200, 0x00040000 | 0x00000001);
            _telemetry.Event("native-opening", new { shown, error = shown ? 0 : Marshal.GetLastWin32Error() });
            if (!shown) Native.ShowWindow(handle, 8);
        }
        finally { _nativePrintPixels = null; }
    }

    private void PrepareNativePrint()
    {
        using var timing = _telemetry.Measure("native-print");
        // AnimateWindow запрашивает WM_PRINT: WPF сам не рисует DirectX-содержимое в GDI DC.
        // Передаём Windows один готовый кадр; движение и отсечение целиком выполняет ОС.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = Math.Max(1, (int)Math.Round(ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Round(ActualHeight * scale));
        var frame = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        frame.Render((Visual)Content);
        byte[] pixels = new byte[width * height * 4];
        frame.CopyPixels(pixels, width * 4, 0);
        _nativePrintPixels = pixels;
        _nativePrintInfo = new PrintBitmapInfo { Size = (uint)Marshal.SizeOf<PrintBitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
    }

    private bool PrintNativeClient(nint deviceContext)
    {
        if (_nativePrintPixels is not byte[] pixels) return false;
        int width = _nativePrintInfo.Width, height = -_nativePrintInfo.Height;
        int lines = StretchDIBits(deviceContext, 0, 0, width, height, 0, 0, width, height, pixels, ref _nativePrintInfo, 0, 0x00CC0020);
        return lines is not (0 or -1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrintBitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(nint dc, int x, int y, int width, int height, int sourceX, int sourceY,
        int sourceWidth, int sourceHeight, byte[] pixels, ref PrintBitmapInfo info, uint usage, uint operation);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);

    private void UpdateCaptureMotion(bool running, Color color)
    {
        CaptureStateGlyph.Foreground = _motionStatusBrush;
        HeaderCaptureGlyph.Foreground = _motionStatusBrush;
        bool animate = IsVisible && SystemParameters.ClientAreaAnimation;
        if (_motionStatusColor != color)
        {
            Color from = _motionStatusBrush.Color;
            _motionStatusBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _motionStatusBrush.Color = color;
            if (animate)
                _motionStatusBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(from, color, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
            _motionStatusColor = color;
        }
        if (_motionRecording is bool previous && previous != running && animate)
        {
            // Один акцент на переход, а не на каждом обновлении состояния таймером.
            AccentGlyph(CaptureToggleGlyph, running ? .76 : 1.18);
            AccentGlyph(HeaderCaptureGlyph, running ? .82 : 1.12);
        }
        _motionRecording = running;
    }

    private static void AccentGlyph(FrameworkElement glyph, double from)
    {
        var scale = new ScaleTransform(from, from);
        glyph.RenderTransformOrigin = new Point(.5, .5);
        glyph.RenderTransform = scale;
        var animation = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) =>
        {
            scale.ScaleX = scale.ScaleY = 1;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void OnSettingsStripSizeChanged(object sender, SizeChangedEventArgs args) => UpdateSettingsIndicator(false);

    private void StopInterfaceMotion()
    {
        foreach (FrameworkElement glyph in new FrameworkElement[] { CaptureToggleGlyph, HeaderCaptureGlyph })
        {
            if (glyph.RenderTransform is not ScaleTransform scale) continue;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
        }
        _motionStatusBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        SettingsSelectionOffset.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void OnSettingsStripVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (SettingsTabStrip.IsVisible) UpdateSettingsIndicator(false);
    }

    private void UpdateSettingsIndicator(bool animate)
    {
        if (SettingsSelectionIndicator is null || SettingsTabs.ActualWidth <= 0) return;
        RadioButton[] tabs = SettingsTabs.Children.OfType<RadioButton>().ToArray();
        int index = Array.FindIndex(tabs, tab => tab.IsChecked == true);
        if (index < 0) return;
        double width = SettingsTabs.ActualWidth / tabs.Length;
        double target = width * index;
        SettingsSelectionIndicator.Width = width;
        if (_settingsIndicatorTarget == target && animate) return;
        double from = SettingsSelectionOffset.X;
        SettingsSelectionOffset.BeginAnimation(TranslateTransform.XProperty, null);
        SettingsSelectionOffset.X = target;
        if (animate && _settingsIndicatorTarget is not null && IsVisible && SystemParameters.ClientAreaAnimation)
            SettingsSelectionOffset.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        _settingsIndicatorTarget = target;
    }
}
