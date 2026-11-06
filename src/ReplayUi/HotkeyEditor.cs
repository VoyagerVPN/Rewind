using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Rewind.Preview;

public sealed class HotkeyEditor : Button
{
    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(nameof(Chord), typeof(HotkeyChord), typeof(HotkeyEditor),
        new FrameworkPropertyMetadata(null, (sender, _) => { var editor = (HotkeyEditor)sender; editor.RefreshLabel(); editor.ChordChanged?.Invoke(editor, EventArgs.Empty); }));
    public HotkeyChord? Chord { get => (HotkeyChord?)GetValue(ChordProperty); set => SetValue(ChordProperty, value); }
    public event EventHandler? ChordChanged;
    public event EventHandler? CaptureStarted;
    public event EventHandler? CaptureEnded;
    private bool _listening;
    public bool RequireModifiers { get; set; } = true;
    public bool AllowHeldKey { get; set; }

    public HotkeyEditor()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        MinHeight = 42; MinWidth = 188;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        ToolTip = "Нажми и введи сочетание · Esc — отмена · Backspace — убрать";
        Click += (_, _) => { if (_listening) return; _listening = true; Focus(); CaptureStarted?.Invoke(this, EventArgs.Empty); RefreshLabel(); };
        LostKeyboardFocus += (_, _) => CancelCapture();
        PreviewKeyDown += OnCaptureKey;
        RefreshLabel();
    }

    public void CancelCapture()
    {
        bool wasListening = _listening; _listening = false; RefreshLabel();
        if (wasListening) CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshLabel()
    {
        Content = _listening ? "Нажми клавиши…" : Chord?.Label ?? "Не назначено";
        SetResourceReference(BackgroundProperty, _listening ? "Hover" : "Raised");
        SetResourceReference(BorderBrushProperty, _listening ? "Accent" : "Line");
    }

    private void OnCaptureKey(object sender, KeyEventArgs args)
    {
        if (!_listening) return;
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.Tab) { CancelCapture(); return; }
        args.Handled = true;
        if (args.IsRepeat) return;
        if (key == Key.Escape) { CancelCapture(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (key == Key.Back && modifiers == ModifierKeys.None) { Chord = null; CancelCapture(); return; }
        if ((modifiers & ModifierKeys.Windows) != 0) { Content = "Без клавиши Win"; return; }
        uint mask = ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0) | ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0);
        uint? heldKey = null;
        if (AllowHeldKey && mask == 0)
        {
            if (key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9)
            { Content = "Удерживай клавишу и нажми F-клавишу…"; return; }
            if (key is >= Key.F1 and <= Key.F24)
                heldKey = Enumerable.Range(0x30, 0x5b - 0x30)
                    .Where(value => value is <= 0x39 or >= 0x41)
                    .Where(value => Keyboard.IsKeyDown(KeyInterop.KeyFromVirtualKey(value)))
                    .Select(value => (uint?)value).FirstOrDefault();
        }
        var chord = new HotkeyChord(mask, (uint)KeyInterop.VirtualKeyFromKey(key), heldKey);
        try { chord.Validate(RequireModifiers); }
        catch (ArgumentException error) { Content = error.Message; return; }
        Chord = chord; CancelCapture();
    }
}
