using System.Windows;

namespace RouteWatch;

public partial class MainWindow : Window
{
    private bool _isClosingAfterShutdown;
    private bool _shutdownInProgress;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosingAfterShutdown)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
            return;

        _shutdownInProgress = true;
        try
        {
            if (DataContext is ViewModels.MainViewModel vm)
                await vm.StopCommand.ExecuteAsync(null);

            _isClosingAfterShutdown = true;
            Close();
        }
        catch (Exception ex)
        {
            _shutdownInProgress = false;
            MessageBox.Show(
                $"RouteWatch could not stop the active trace safely. The application will remain open.\n\n{ex.Message}",
                "RouteWatch — Shutdown Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
