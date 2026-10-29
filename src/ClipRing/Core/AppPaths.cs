namespace ClipRing.Core;

public static class AppPaths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipRing");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);

        // Left over from the removed VALORANT detection (kill-banner templates).
        try
        {
            var templates = Path.Combine(DataDir, "valorant-templates");
            if (Directory.Exists(templates)) Directory.Delete(templates, recursive: true);
        }
        catch { /* not important */ }
    }
}
