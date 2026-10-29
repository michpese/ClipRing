using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClipRing.Hotkeys;
using ClipRing.Localization;

namespace ClipRing.UI;

/// <summary>One line in a settings sheet: title and help text on the left, the control on the right.</summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowDividerProperty =
        DependencyProperty.Register(nameof(ShowDivider), typeof(bool), typeof(SettingRow), new PropertyMetadata(true));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public bool ShowDivider { get => (bool)GetValue(ShowDividerProperty); set => SetValue(ShowDividerProperty, value); }
}

/// <summary>Click it, press a key combination, done. Backspace/Delete clears, Escape cancels.</summary>
public class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                                      (d, _) => ((HotkeyBox)d).ShowCurrent()));

    /// <summary>Raised while a box is recording, so global hotkeys can be released meanwhile.</summary>
    public static event Action? CaptureStarted;
    public static event Action? CaptureEnded;

    public HotkeyBox()
    {
        // Pick up the app's TextBox style (Fluent theme); implicit styles don't apply to subclasses.
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        MinWidth = 190;
        ShowCurrent();
        Loc.LanguageChanged += ShowCurrent;
    }

    public string Hotkey { get => (string)GetValue(HotkeyProperty); set => SetValue(HotkeyProperty, value); }

    private void ShowCurrent()
    {
        if (!IsKeyboardFocused) Text = HotkeyBinding.DisplayText(Hotkey);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Text = Loc.T("Hotkey_Press");
        CaptureStarted?.Invoke();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ShowCurrent();
        CaptureEnded?.Invoke();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed or Key.DeadCharProcessed:
                return; // wait for the actual key
            case Key.Escape:
                Keyboard.ClearFocus();
                return;
            case Key.Back or Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                Hotkey = "";
                Keyboard.ClearFocus();
                return;
        }

        var mods = HotkeyModifiers.None;
        var km = Keyboard.Modifiers;
        if (km.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (km.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (km.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (km.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

        bool standalone = key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll or Key.Snapshot or Key.Insert;
        if (mods == HotkeyModifiers.None && !standalone)
        {
            Text = Loc.T("Hotkey_NeedModifier");
            return;
        }

        Hotkey = new HotkeyBinding(mods, key).ToString();
        Keyboard.ClearFocus();
    }
}

/// <summary>
/// The replay buffer as a strip of ticks: "now" at the right edge, filled ticks show how far
/// back the RAM buffer already reaches. Flashes once when a clip was saved.
/// </summary>
public class BufferTape : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(double), typeof(BufferTape), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalSecondsProperty = DependencyProperty.Register(
        nameof(TotalSeconds), typeof(double), typeof(BufferTape), new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(BufferTape), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FlashCountProperty = DependencyProperty.Register(
        nameof(FlashCount), typeof(int), typeof(BufferTape), new PropertyMetadata(0, (d, _) => ((BufferTape)d).Flash()));

    private static readonly DependencyProperty FlashLevelProperty = DependencyProperty.Register(
        "FlashLevel", typeof(double), typeof(BufferTape), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public BufferTape() => Loc.LanguageChanged += InvalidateVisual;

    public double Fill { get => (double)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double TotalSeconds { get => (double)GetValue(TotalSecondsProperty); set => SetValue(TotalSecondsProperty, value); }
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public int FlashCount { get => (int)GetValue(FlashCountProperty); set => SetValue(FlashCountProperty, value); }

    private void Flash()
    {
        if (!SystemParameters.ClientAreaAnimation) return; // respects "reduce animations"
        BeginAnimation(FlashLevelProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private Brush ResourceBrush(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 24) return;

        var filledBrush = ResourceBrush("Brush.Text", Color.FromRgb(0xE6, 0xEA, 0xF2));
        var emptyBrush = ResourceBrush("Brush.Line", Color.FromRgb(0x2C, 0x34, 0x42));
        var labelBrush = ResourceBrush("Brush.Faint", Color.FromRgb(0x5D, 0x66, 0x76));
        var liveBrush = ResourceBrush("Brush.Live", Color.FromRgb(0xFF, 0x4D, 0x5A));

        const double labelHeight = 18;
        double tickArea = h - labelHeight;
        double total = Math.Max(1, TotalSeconds);
        int count = Math.Max(10, (int)(w / 6));
        double step = w / count;
        int filled = (int)Math.Round(Math.Clamp(Fill, 0, 1) * count);
        double major = total <= 60 ? 15 : total <= 180 ? 30 : total <= 600 ? 60 : 120;
        double secondsPerTick = total / count;

        for (int i = 0; i < count; i++)
        {
            double secondsAgo = total - (i + 0.5) * secondsPerTick;
            bool isMajor = Math.Abs(secondsAgo % major) < secondsPerTick / 2 ||
                           Math.Abs(major - secondsAgo % major) < secondsPerTick / 2;
            double tickHeight = isMajor ? tickArea : tickArea * 0.55;
            bool isFilled = i >= count - filled;
            var rect = new Rect(i * step + step / 2 - 1, tickArea - tickHeight, 2, tickHeight);
            dc.DrawRectangle(isFilled ? filledBrush : emptyBrush, null, rect);
        }

        double flash = (double)GetValue(FlashLevelProperty);
        if (flash > 0.01 && filled > 0)
        {
            var overlay = filledBrush.Clone();
            overlay.Opacity = 0.18 * flash;
            dc.DrawRectangle(overlay, null, new Rect(w - filled * step, 0, filled * step, tickArea));
        }

        if (IsLive) dc.DrawRectangle(liveBrush, null, new Rect(w - 2, 0, 2, tickArea));

        // Time labels under the major ticks: -2:00 … jetzt
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        for (double t = 0; t <= total + 0.001; t += major)
        {
            string text = t == 0 ? Loc.T("Tape_Now") : "-" + TimeSpan.FromSeconds(t).ToString(t >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11, labelBrush, dpi);
            double x = w - t / total * w;
            x = t == 0 ? x - ft.Width : Math.Max(0, x - ft.Width / 2);
            if (x + ft.Width > w) x = w - ft.Width;
            dc.DrawText(ft, new Point(x, tickArea + 4));
        }
    }
}

/// <summary>Segmented level meter on a dB scale (-48 dB … 0 dB).</summary>
public class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 2) return;

        var on = TryFindResource("Brush.Text") as Brush ?? Brushes.White;
        var off = TryFindResource("Brush.Line") as Brush ?? Brushes.DimGray;
        var hot = TryFindResource("Brush.Live") as Brush ?? Brushes.Red;
        var warm = TryFindResource("Brush.Warn") as Brush ?? Brushes.Orange;

        double peak = Math.Max(Level, 1e-6);
        double db = 20 * Math.Log10(peak);
        double norm = Math.Clamp((db + 48) / 48, 0, 1);

        const int segments = 28;
        double gap = 2;
        double segW = (w - gap * (segments - 1)) / segments;
        int lit = (int)Math.Round(norm * segments);
        for (int i = 0; i < segments; i++)
        {
            Brush b = i >= lit ? off : i >= segments - 2 ? hot : i >= segments - 6 ? warm : on;
            dc.DrawRectangle(b, null, new Rect(i * (segW + gap), 0, segW, h));
        }
    }
}
