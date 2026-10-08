using System.Windows;

namespace RouteWatch;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Gracefully stop the probe engine before the window closes
        if (DataContext is ViewModels.MainViewModel vm)
            await vm.StopCommand.ExecuteAsync(null);
        base.OnClosing(e);
    }
}
