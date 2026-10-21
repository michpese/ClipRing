using System.ComponentModel;
using System.Windows;
using ClipRing.UI;

namespace ClipRing;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

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
