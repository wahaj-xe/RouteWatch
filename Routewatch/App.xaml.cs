using System.Windows;

namespace RouteWatch;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Global exception handler
        DispatcherUnhandledException += (_, ex) =>
        {
            var msg = ex.Exception.InnerException != null 
                ? $"{ex.Exception.Message}\n\nDetail: {ex.Exception.InnerException.Message}"
                : ex.Exception.Message;

            MessageBox.Show(
                $"Unhandled error:\n\n{msg}",
                "RouteWatch — Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
