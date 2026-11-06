using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Rewind.Preview;

internal sealed partial class ActionToastWindow : Window
{
    private readonly DispatcherTimer _expiry = new(DispatcherPriority.Background);
    private readonly Action<string> _openFile;
    private string? _file;
    private TimeSpan _lifetime;
    public string NoticeKey { get; }
    public nint Monitor { get; }

    public ActionToastWindow(string key, nint monitor, Action<string> openFile)
    {
        InitializeComponent();
        NoticeKey = key; Monitor = monitor; _openFile = openFile;
        _expiry.Tick += (_, _) => Close();
        Closed += (_, _) => _expiry.Stop();
        SourceInitialized += (_, _) => UpdateNativeStyle();
        MouseEnter += (_, _) => _expiry.Stop();
        MouseLeave += (_, _) => RestartExpiry();
    }

    public void Present(string title, string detail, SymbolRegular symbol, string? file, bool error, bool persistent)
    {
        _file = file;
        ToastTitle.Text = title;
        int newline = file is null ? -1 : detail.IndexOf('\n');
        string parameters = newline < 0 ? detail : detail[..newline];
        string filenames = newline < 0 ? "" : detail[(newline + 1)..];
        ToastDetail.Text = parameters;
        ToastDetail.TextWrapping = file is null ? TextWrapping.Wrap : TextWrapping.NoWrap;
        int countSeparator = file is null ? -1 : parameters.LastIndexOf(" · ", StringComparison.Ordinal);
        if (countSeparator >= 0)
        {
            ToastDetail.Inlines.Clear();
            ToastDetail.Inlines.Add(new Run(parameters[..(countSeparator + 3)]));
            ToastDetail.Inlines.Add(new Run(parameters[(countSeparator + 3)..]) { FontWeight = FontWeights.SemiBold });
        }
        ToastFileName.Text = filenames;
        ToastFileName.ToolTip = filenames;
        ToastFileName.Visibility = filenames.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToastDetail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToastIcon.Symbol = symbol;
        ToastIcon.Foreground = new SolidColorBrush(error ? Color.FromRgb(230, 164, 155) : Color.FromRgb(235, 235, 232));
        ToastAction.Cursor = file is null ? Cursors.Arrow : Cursors.Hand;
        ToastAction.ToolTip = file is null ? null : "Показать файл в Проводнике";
        System.Windows.Automation.AutomationProperties.SetName(ToastAction, title);
        _lifetime = persistent ? TimeSpan.Zero : TimeSpan.FromSeconds(error ? 6 : 3.5);
        if (new WindowInteropHelper(this).Handle != 0) UpdateNativeStyle();
        RestartExpiry();
    }

    private void RestartExpiry()
    {
        _expiry.Stop();
        if (_lifetime <= TimeSpan.Zero) return;
        _expiry.Interval = _lifetime; _expiry.Start();
    }

    public int Place(int offset)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(Monitor, ref info))
        {
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(handle, 2), ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (Native.MonitorFromWindow(handle, 2) != Monitor)
            Native.SetWindowPos(handle, -1, info.Work.Left, info.Work.Top, 0, 0, 0x11);
        double scale = Math.Max(96, Native.GetDpiForWindow(handle)) / 96d;
        Width = Math.Min(340, Math.Max(120, (info.Work.Right - info.Work.Left - 32) / scale));
        UpdateLayout();
        int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        int left = info.Work.Right - width - (int)(20 * scale), top = info.Work.Top + (int)(20 * scale) + offset;
        if (!Native.SetWindowPos(handle, -1, left, top, 0, 0, 0x11)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return height + (int)(10 * scale);
    }

    private void UpdateNativeStyle()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        // Тост не активирует окно; некликабельные сообщения пропускают мышь в игру.
        style |= 0x08000000L | 0x80;
        style = _file is null ? style | 0x20 : style & ~0x20L;
        SetWindowLongPtr(handle, -20, (nint)style);
    }

    private void OnToastClick(object sender, RoutedEventArgs args)
    {
        if (_file is not string file) return;
        Close(); _openFile(file);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

internal sealed class ActionToastPresenter(Action<string> openFile) : IDisposable
{
    private readonly List<ActionToastWindow> _windows = [];
    private bool _disposed;

    public void Show(string key, string title, string detail, SymbolRegular symbol, nint monitor, string? file = null, bool error = false, bool persistent = false) =>
        Show(key, title, detail, symbol, [monitor], file, error, persistent);

    public void Show(string key, string title, string detail, SymbolRegular symbol, IReadOnlyList<nint> monitors,
        string? file = null, bool error = false, bool persistent = false)
    {
        if (_disposed) return;
        foreach (ActionToastWindow obsolete in _windows.Where(window => window.NoticeKey == key && !monitors.Contains(window.Monitor)).ToArray())
            obsolete.Close();
        foreach (nint monitor in monitors)
        {
            ActionToastWindow? window = _windows.FirstOrDefault(candidate => candidate.NoticeKey == key && candidate.Monitor == monitor);
            if (window is null)
            {
                ActionToastWindow[] stack = _windows.Where(candidate => candidate.Monitor == monitor).ToArray();
                if (stack.Length >= 3) stack[^1].Close();
                window = new ActionToastWindow(key, monitor, openFile);
                _windows.Insert(0, window);
                window.Closed += (_, _) => { _windows.Remove(window); if (!_disposed) Reposition(); };
            }
            try
            {
                window.Present(title, detail, symbol, file, error, persistent);
                if (!window.IsVisible) window.Show();
                Reposition();
            }
            catch { window.Close(); throw; }
        }
    }

    private void Reposition()
    {
        foreach (var group in _windows.GroupBy(window => window.Monitor))
        {
            int offset = 0;
            foreach (ActionToastWindow window in group) offset += window.Place(offset);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (ActionToastWindow window in _windows.ToArray()) window.Close();
        _windows.Clear();
    }
}
