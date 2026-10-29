using System.Windows.Input;
using System.Windows.Interop;
using ClipRing.Core;
using ClipRing.Localization;
using ClipRing.Native;

namespace ClipRing.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = Win32.MOD_ALT,
    Control = Win32.MOD_CONTROL,
    Shift = Win32.MOD_SHIFT,
    Win = Win32.MOD_WIN,
}

/// <remarks>
/// Stored language-neutral ("Ctrl+Alt+F10"); <see cref="Display()"/> shows it in the UI language
/// ("Strg+Alt+F10" in German). Parsing accepts both, so older settings keep working.
/// </remarks>
public readonly record struct HotkeyBinding(HotkeyModifiers Modifiers, Key Key)
{
    /// <summary>Canonical text for settings.json.</summary>
    public override string ToString() => Format(german: false, canonical: true);

    /// <summary>Text for the UI in the current language.</summary>
    public string Display() => Format(german: Loc.Instance.Language == "de", canonical: false);

    private string Format(bool german, bool canonical)
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add(german ? "Strg" : "Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(canonical ? CanonicalKeyName(Key) : german ? GermanKeyName(Key) : EnglishKeyName(Key));
        return string.Join("+", parts);
    }

    private static string CanonicalKeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.Snapshot => "PrintScreen",
        Key.Scroll => "ScrollLock",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        _ => key.ToString(),
    };

    private static string GermanKeyName(Key key) => key switch
    {
        Key.Snapshot => "Druck",
        Key.Scroll => "Rollen",
        Key.Insert => "Einfg",
        Key.Delete => "Entf",
        Key.Home => "Pos1",
        Key.End => "Ende",
        Key.PageUp => "BildAuf",
        Key.PageDown => "BildAb",
        _ => CanonicalKeyName(key),
    };

    private static string EnglishKeyName(Key key) => key switch
    {
        Key.Snapshot => "PrtSc",
        Key.Scroll => "ScrLk",
        Key.Insert => "Ins",
        Key.Delete => "Del",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        _ => CanonicalKeyName(key),
    };

    private static Key ParseKey(string text)
    {
        if (text.Length == 1 && char.IsDigit(text[0])) return Key.D0 + (text[0] - '0');
        if (text.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && text.Length == 4 && char.IsDigit(text[3]))
            return Key.NumPad0 + (text[3] - '0');
        return text.ToLowerInvariant() switch
        {
            "druck" or "prtsc" or "printscreen" => Key.Snapshot,
            "rollen" or "scrlk" or "scrolllock" => Key.Scroll,
            "einfg" or "ins" => Key.Insert,
            "entf" or "del" => Key.Delete,
            "pos1" => Key.Home,
            "ende" => Key.End,
            "bildauf" or "pgup" => Key.PageUp,
            "bildab" or "pgdn" => Key.PageDown,
            _ => Enum.TryParse<Key>(text, ignoreCase: true, out var k) ? k : Key.None,
        };
    }

    public static HotkeyBinding? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = HotkeyModifiers.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "strg": case "control": mods |= HotkeyModifiers.Control; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": case "umschalt": mods |= HotkeyModifiers.Shift; break;
                case "win": mods |= HotkeyModifiers.Win; break;
                default: key = ParseKey(raw); break;
            }
        }
        return key == Key.None ? null : new HotkeyBinding(mods, key);
    }

    /// <summary>Settings value → canonical form (unparseable values stay as they are).</summary>
    public static string Normalize(string? text) => Parse(text)?.ToString() ?? text ?? "";

    /// <summary>Settings value → UI text in the current language.</summary>
    public static string DisplayText(string? text)
        => string.IsNullOrWhiteSpace(text) ? Loc.T("Hotkey_None") : Parse(text)?.Display() ?? text;
}

/// <summary>
/// Global hotkeys via RegisterHotKey on a message-only window. Works while games have focus
/// (no keyboard hooks, so nothing for anti-cheat to complain about).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private sealed record Entry(int Id, HotkeyBinding Binding, Action Callback);

    private readonly HwndSource _source;
    private readonly Dictionary<string, Entry> _entries = new();
    private int _nextId = 0xB000;
    private bool _suspended;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("ClipRingHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers (or replaces) a named hotkey. Returns an error text or null.</summary>
    public string? Register(string name, string? text, Action callback)
    {
        Unregister(name);
        var binding = HotkeyBinding.Parse(text);
        if (binding is null) return null; // empty = disabled

        var entry = new Entry(_nextId++, binding.Value, callback);
        _entries[name] = entry;
        if (_suspended) return null;
        return RegisterNative(entry) ? null : Loc.F("Hotkey_Taken", binding.Value.Display());
    }

    public void Unregister(string name)
    {
        if (_entries.Remove(name, out var entry))
            Win32.UnregisterHotKey(_source.Handle, entry.Id);
    }

    private bool RegisterNative(Entry e)
    {
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(e.Binding.Key);
        bool ok = Win32.RegisterHotKey(_source.Handle, e.Id, (uint)e.Binding.Modifiers | Win32.MOD_NOREPEAT, vk);
        if (!ok) Log.Warn($"Hotkey {e.Binding} konnte nicht registriert werden (belegt?)");
        return ok;
    }

    /// <summary>Temporarily releases all hotkeys (while the user records a new combination).</summary>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        foreach (var e in _entries.Values) Win32.UnregisterHotKey(_source.Handle, e.Id);
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        foreach (var e in _entries.Values) RegisterNative(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            foreach (var e in _entries.Values)
            {
                if (e.Id != id) continue;
                handled = true;
                try { e.Callback(); }
                catch (Exception ex) { Log.Error("Hotkey-Aktion fehlgeschlagen", ex); }
                break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var e in _entries.Values) Win32.UnregisterHotKey(_source.Handle, e.Id);
        _entries.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
