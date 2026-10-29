using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace ClipRing.Localization;

/// <summary>
/// UI texts in German and English. The tables are embedded JSON files (Localization/de.json, en.json).
/// XAML binds through <see cref="TExtension"/>, so switching the language updates the open window at once;
/// code uses <see cref="T"/> and <see cref="F"/> and listens to <see cref="LanguageChanged"/> for texts it
/// has already handed out.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    /// <summary>Keys that were asked for but are missing in the current table (checked by the CI self-test).</summary>
    public static ConcurrentDictionary<string, byte> Missing { get; } = new();

    public static event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private volatile Dictionary<string, string> _strings = new();
    private volatile Dictionary<string, string> _fallback = new();

    private Loc() => Apply("system");

    /// <summary>"de" or "en".</summary>
    public string Language { get; private set; } = "";

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public string this[string key] => Lookup(key);

    public static string T(string key) => Instance.Lookup(key);

    public static string F(string key, params object?[] args)
    {
        var format = Instance.Lookup(key);
        try { return string.Format(Instance.Culture, format, args); }
        catch (FormatException) { return format; }
    }

    /// <summary>"system" follows the Windows display language (German or else English).</summary>
    public static string Resolve(string? setting) => setting switch
    {
        "de" => "de",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en",
    };

    public void Apply(string? setting)
    {
        string language = Resolve(setting);
        if (language == Language) return;

        var strings = Load(language);
        _fallback = language == "en" ? strings : Load("en");
        _strings = strings;
        Language = language;
        Culture = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    public static IReadOnlyDictionary<string, string> Table(string language) => Load(language);

    private string Lookup(string key)
    {
        if (_strings.TryGetValue(key, out var text)) return text;
        Missing.TryAdd(key, 0);
        return _fallback.TryGetValue(key, out text) ? text : key;
    }

    private static Dictionary<string, string> Load(string language)
    {
        try
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"ClipRing.Strings.{language}.json");
            if (stream is null) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>XAML: Text="{l:T Nav_Overview}". A live binding, so it follows language switches.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
