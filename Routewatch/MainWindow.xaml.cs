using System;
using System.Windows;

namespace RouteWatch;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        try
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.Shutdown();
            }
        }
        catch { }

        Application.Current?.Shutdown();
    }
}
