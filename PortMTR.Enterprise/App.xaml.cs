using System.Windows;

namespace PortMTR.Enterprise;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Global exception handler
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(
                $"Unhandled error:\n\n{ex.Exception.Message}\n\n{ex.Exception.StackTrace}",
                "PortMTR Enterprise — Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
