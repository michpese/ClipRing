using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using ClipRing.Localization;
using ClipRing.UI;

namespace ClipRing;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        EnableOptionLocking();
        ApplyLanguage();
        Loc.LanguageChanged += ApplyLanguage;
    }

    /// <summary>
    /// ComboBox entries follow LiveOption.IsEnabled, so options this PC cannot use are greyed out.
    /// Built in code on top of the theme's own ComboBoxItem style, so the Fluent look stays.
    /// </summary>
    private void EnableOptionLocking()
    {
        var themeStyle = TryFindResource(typeof(ComboBoxItem)) as Style;
        var style = new Style(typeof(ComboBoxItem), themeStyle);
        style.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(LiveOption<int>.IsEnabled)) { FallbackValue = true }));
        Resources[typeof(ComboBoxItem)] = style;
    }

    /// <summary>Number and percent formats in bindings follow the UI language.</summary>
    private void ApplyLanguage()
        => Language = XmlLanguage.GetLanguage(Loc.Instance.Culture.IetfLanguageTag);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        // Never really close from the X: either hide to the tray or shut down the whole app.
        e.Cancel = true;
        if (DataContext is MainViewModel { Settings.CloseToTray: true })
        {
            Hide();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => ((App)Application.Current).ExitApp()));
        }
    }
}
