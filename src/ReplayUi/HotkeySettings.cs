using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rewind.Preview;

public enum HotkeyAction { Panel = 1, Save = 2, SaveAll = 3, CaptureToggle = 4 }
public enum PlayerHotkeyAction { Pause, PreviousFrame, NextFrame, Backward, Forward, GoToStart, Delete }

public sealed record PlayerHotkeySettings(HotkeyChord? Pause, HotkeyChord? PreviousFrame, HotkeyChord? NextFrame, HotkeyChord? Backward, HotkeyChord? Forward)
{
    public HotkeyChord? GoToStart { get; init; } = new(0, 0x24);
    public HotkeyChord? Delete { get; init; } = new(0, 0x2e);
    public static PlayerHotkeySettings Default => new(new(0, 0x20), new(0, 0xbc), new(0, 0xbe), new(0, 0x25), new(0, 0x27));
    public HotkeyChord? Get(PlayerHotkeyAction action) => action switch { PlayerHotkeyAction.Pause => Pause, PlayerHotkeyAction.PreviousFrame => PreviousFrame,
        PlayerHotkeyAction.NextFrame => NextFrame, PlayerHotkeyAction.Backward => Backward, PlayerHotkeyAction.Forward => Forward,
        PlayerHotkeyAction.GoToStart => GoToStart, PlayerHotkeyAction.Delete => Delete, _ => throw new ArgumentOutOfRangeException(nameof(action)) };
    public PlayerHotkeySettings With(PlayerHotkeyAction action, HotkeyChord? chord) => action switch {
        PlayerHotkeyAction.Pause => this with { Pause = chord }, PlayerHotkeyAction.PreviousFrame => this with { PreviousFrame = chord },
        PlayerHotkeyAction.NextFrame => this with { NextFrame = chord }, PlayerHotkeyAction.Backward => this with { Backward = chord },
        PlayerHotkeyAction.Forward => this with { Forward = chord }, PlayerHotkeyAction.GoToStart => this with { GoToStart = chord },
        PlayerHotkeyAction.Delete => this with { Delete = chord }, _ => throw new ArgumentOutOfRangeException(nameof(action)) };
}

public sealed record HotkeyChord(uint Modifiers, uint Key, uint? HeldKey = null)
{
    public string Label => string.Join(" + ", new[] { (Modifiers & 2) != 0 ? "Ctrl" : null,
        (Modifiers & 1) != 0 ? "Alt" : null, (Modifiers & 4) != 0 ? "Shift" : null,
        HeldKey is uint held ? KeyLabel(held) : null, KeyLabel(Key) }.OfType<string>());

    private static string KeyLabel(uint key) => key switch
    {
        >= 0x70 and <= 0x87 => "F" + (key - 0x6f),
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a => ((char)key).ToString(),
        0x20 => "Space", 0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓", 0x2c => "Print Screen", 0x2d => "Insert", 0x2e => "Delete",
        0xbc => ",", 0xbe => ".",
        _ => "0x" + key.ToString("X2")
    };

    public void Validate(bool requireModifiers = true)
    {
        if (Modifiers > 7 || requireModifiers && Modifiers == 0 && HeldKey is null) throw new ArgumentException(Localization.Text("Message.AddCtrlAltShiftOrAHeldLetter"));
        if (HeldKey is uint held && (held is not (>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a) || Key is not (>= 0x70 and <= 0x87)))
            throw new ArgumentException(Localization.Text("Message.HoldALetterOrNumberAndPressAnFKey"));
        if (Key is not (>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a or >= 0x70 and <= 0x87 or 0x20 or 0xbc or 0xbe or >= 0x21 and <= 0x28 or >= 0x2c and <= 0x2e))
            throw new ArgumentException(Localization.Text("Message.ChooseALetterNumberFKeyOrNavigationKey"));
    }
}

public sealed record HotkeySettings(HotkeyChord? Panel, HotkeyChord? Save, HotkeyChord? SaveAll, PlayerHotkeySettings? Player = null)
{
    public HotkeyChord? CaptureToggle { get; init; } = new(0, 0x79, 0x53);
    public HotkeyChord? Settings { get; init; } = new(2, 0xbc);
    [JsonIgnore] public PlayerHotkeySettings PlayerBindings => Player ?? PlayerHotkeySettings.Default;
    public static HotkeySettings Default => new(new(3, 0x5a), new(1, 0x79), new(5, 0x79));
    public HotkeyChord? Get(HotkeyAction action) => action switch
    { HotkeyAction.Panel => Panel, HotkeyAction.Save => Save, HotkeyAction.SaveAll => SaveAll, HotkeyAction.CaptureToggle => CaptureToggle, _ => throw new ArgumentOutOfRangeException(nameof(action)) };
    public HotkeySettings With(HotkeyAction action, HotkeyChord? chord) => action switch
    { HotkeyAction.Panel => this with { Panel = chord }, HotkeyAction.Save => this with { Save = chord }, HotkeyAction.SaveAll => this with { SaveAll = chord }, HotkeyAction.CaptureToggle => this with { CaptureToggle = chord }, _ => throw new ArgumentOutOfRangeException(nameof(action)) };
    public void Validate()
    {
        HotkeyChord[] chords = new[] { Panel, Save, SaveAll, CaptureToggle }.OfType<HotkeyChord>().ToArray();
        foreach (HotkeyChord chord in chords) chord.Validate();
        HotkeyChord[] local = Enum.GetValues<PlayerHotkeyAction>().Select(action => PlayerBindings.Get(action)).OfType<HotkeyChord>().ToArray();
        foreach (HotkeyChord chord in local) chord.Validate(requireModifiers: false);
        Settings?.Validate(requireModifiers: false);
        if (Settings?.HeldKey is not null || local.Any(chord => chord.HeldKey is not null))
            throw new ArgumentException(Localization.Text("Message.AHeldLetterIsSupportedOnlyForGlobalActions"));
        HotkeyChord[] all = chords.Concat(local).Concat(new[] { Settings }.OfType<HotkeyChord>()).ToArray();
        if (all.Distinct().Count() != all.Length) throw new ArgumentException(Localization.Text("Message.ThisShortcutIsAlreadyAssignedToAnotherRewindAction"));
    }
}

public sealed class HeldKeyPressTracker(HotkeyChord chord, bool initiallyPressed = false)
{
    private bool _triggerDown = initiallyPressed;

    public bool Update(uint key, bool pressed, uint modifiers, bool heldDown, bool windowsDown)
    {
        if (key != chord.Key) return false;
        bool firstPress = pressed && !_triggerDown;
        _triggerDown = pressed;
        return firstPress && heldDown && chord.HeldKey is not null && modifiers == chord.Modifiers && !windowsDown;
    }
}

public sealed class HotkeySettingsFile(string path)
{
    public HotkeySettings Load()
    {
        if (!File.Exists(path)) return HotkeySettings.Default;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var settings = document.RootElement.Deserialize<HotkeySettings>() ?? throw new InvalidDataException(Localization.Text("Message.CouldNotReadHotkeys"));
        bool Conflicts(HotkeyChord? chord) => chord is not null && Enum.GetValues<HotkeyAction>().Select(settings.Get)
            .Concat(Enum.GetValues<PlayerHotkeyAction>().Select(settings.PlayerBindings.Get)).Append(settings.Settings).Count(binding => binding == chord) > 1;
        // Новые значения по умолчанию не отбирают уже назначенные пользователем клавиши.
        if (!document.RootElement.TryGetProperty(nameof(HotkeySettings.CaptureToggle), out _) && Conflicts(settings.CaptureToggle))
            settings = settings with { CaptureToggle = null };
        if (!document.RootElement.TryGetProperty(nameof(HotkeySettings.Settings), out _) && Conflicts(settings.Settings))
            settings = settings with { Settings = null };
        document.RootElement.TryGetProperty(nameof(HotkeySettings.Player), out JsonElement player);
        foreach (PlayerHotkeyAction action in new[] { PlayerHotkeyAction.GoToStart, PlayerHotkeyAction.Delete })
            if ((player.ValueKind != JsonValueKind.Object || !player.TryGetProperty(action.ToString(), out _)) && Conflicts(settings.PlayerBindings.Get(action)))
                settings = settings with { Player = settings.PlayerBindings.With(action, null) };
        settings.Validate(); return settings;
    }
    public void Save(HotkeySettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".new", path, overwrite: true);
    }
}
