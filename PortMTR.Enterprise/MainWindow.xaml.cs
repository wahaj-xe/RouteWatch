using System.Windows;
using System.Windows.Input;

namespace PortMTR.Enterprise;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Allow Enter key in Target field to start probe
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return && DataContext is ViewModels.MainViewModel vm)
                if (vm.StartCommand.CanExecute(null))
                    vm.StartCommand.Execute(null);
        };
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Gracefully stop the probe engine before the window closes
        if (DataContext is ViewModels.MainViewModel vm)
            await vm.StopCommand.ExecuteAsync(null);
        base.OnClosing(e);
    }
}
