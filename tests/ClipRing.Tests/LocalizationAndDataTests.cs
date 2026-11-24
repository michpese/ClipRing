using System.Text.Json;
using System.Text.RegularExpressions;
using ClipRing.Games;
using ClipRing.Localization;
using Xunit;

namespace ClipRing.Tests;

public class LocalizationAndDataTests
{
    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)(?:[^}]*)\}", RegexOptions.Compiled);

    [Fact]
    public void GermanAndEnglishTablesHaveIdenticalKeysAndNonEmptyValues()
    {
        var de = Loc.Table("de");
        var en = Loc.Table("en");

        Assert.NotEmpty(de);
        Assert.Equal(de.Keys.OrderBy(k => k, StringComparer.Ordinal), en.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var key in de.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(de[key]), $"de.json has blank value for '{key}'");
            Assert.False(string.IsNullOrWhiteSpace(en[key]), $"en.json has blank value for '{key}'");
        }
    }

    [Fact]
    public void FormatPlaceholdersMatchBetweenGermanAndEnglish()
    {
        var de = Loc.Table("de");
        var en = Loc.Table("en");

        foreach (var key in de.Keys)
        {
            var dePlaceholders = PlaceholderPattern.Matches(de[key]).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();
            var enPlaceholders = PlaceholderPattern.Matches(en[key]).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();
            Assert.True(dePlaceholders.SequenceEqual(enPlaceholders),
                $"Placeholder mismatch for '{key}': de=[{string.Join(',', dePlaceholders)}] en=[{string.Join(',', enPlaceholders)}]");
        }
    }

    [Fact]
    public void KnownGamesResourceIsValidAndNonEmpty()
    {
        using var stream = typeof(GameLibrary).Assembly.GetManifestResourceStream("ClipRing.known_games.json");
        Assert.NotNull(stream);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        Assert.NotNull(map);
        Assert.NotEmpty(map);
        foreach (var (exe, name) in map)
        {
            Assert.False(string.IsNullOrWhiteSpace(exe));
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.EndsWith(".exe", exe, StringComparison.OrdinalIgnoreCase);
        }
    }
}
