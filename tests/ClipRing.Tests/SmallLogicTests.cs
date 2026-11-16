using ClipRing.Audio;
using ClipRing.Capture;
using ClipRing.Hotkeys;
using Xunit;

namespace ClipRing.Tests;

public class FileNameTests
{
    [Theory]
    [InlineData("Counter-Strike 2", "Counter-Strike 2")]
    [InlineData("Game: The Sequel?", "Game_ The Sequel_")]
    [InlineData("  ", "Desktop")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Trailing dots...", "Trailing dots")]
    public void SanitizesNames(string input, string expected)
        => Assert.Equal(expected, ClipSaver.SanitizeName(input, "Desktop"));

    [Fact]
    public void LongNameDoesNotEndInADot()
    {
        string name = new string('a', 79) + ".bbbb";
        var result = ClipSaver.SanitizeName(name, "x");
        Assert.True(result.Length <= 80);
        Assert.False(result.EndsWith('.'));
    }

    [Fact]
    public void TemplateGetsTheTag()
    {
        var name = ClipSaver.BuildFileName("{game}_{date}_{time}", "Counter-Strike 2", new DateTime(2026, 11, 1, 18, 5, 9), "3K");
        Assert.Equal("Counter-Strike 2_2026-11-01_18-05-09_3K", name);
    }
}

public class HotkeyTests
{
    [Fact]
    public void ParsesCombinations()
    {
        var binding = HotkeyBinding.Parse("Strg+Alt+F10");
        Assert.NotNull(binding);
        Assert.Equal("Ctrl+Alt+F10", binding.Value.ToString());
    }

    [Theory]
    [InlineData("Ctrl+12")]
    [InlineData("Ctrl+999")]
    public void RejectsNumbersAsKeyNames(string text)
    {
        var binding = HotkeyBinding.Parse(text);
        Assert.True(binding is null || binding.Value.Key == System.Windows.Input.Key.None);
    }
}

public class DownmixTests
{
    [Fact]
    public void QuadKeepsBothRearChannelsOnTheirSide()
    {
        var (left, right) = DownmixToStereoSampleProvider.Weights(4, 0);
        Assert.True(left[2] > 0 && right[2] == 0); // back left
        Assert.True(right[3] > 0 && left[3] == 0); // back right
    }

    [Fact]
    public void FivePointZeroUsesItsRearChannels()
    {
        var (left, right) = DownmixToStereoSampleProvider.Weights(5, 0);
        Assert.True(left[2] > 0 && right[2] > 0); // center on both
        Assert.True(left[3] > 0);                 // back left
        Assert.True(right[4] > 0);                // back right
    }

    [Fact]
    public void FivePointOneSkipsLfeAndUsesTheMask()
    {
        const int mask = 0x1 | 0x2 | 0x4 | 0x8 | 0x200 | 0x400; // FL FR FC LFE SL SR
        var (left, right) = DownmixToStereoSampleProvider.Weights(6, mask);
        Assert.Equal(0, left[3]);
        Assert.Equal(0, right[3]);
        Assert.True(left[4] > 0 && right[5] > 0);
    }
}
