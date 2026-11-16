using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
        IsVisibleChanged += (_, _) => UpdateWindowShown();
        StateChanged += (_, _) => UpdateWindowShown();
        DataContextChanged += (_, _) => UpdateWindowShown();
    }

    private void UpdateWindowShown()
    {
        if (DataContext is MainViewModel vm) vm.WindowShown = IsVisible && WindowState != WindowState.Minimized;
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

    /// <summary>
    /// Text boxes write to the settings when they lose focus. Hiding the window doesn't take the focus away,
    /// so a value typed right before closing would be lost.
    /// </summary>
    private void CommitPendingEdits()
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitPendingEdits();
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
