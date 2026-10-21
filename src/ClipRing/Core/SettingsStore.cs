using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipRing.Core;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly object Gate = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null) return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Einstellungen konnten nicht gelesen werden, verwende Standardwerte", ex);
            try
            {
                File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", overwrite: true);
            }
            catch { /* best effort */ }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Einstellungen konnten nicht gespeichert werden", ex);
            }
        }
    }
}
