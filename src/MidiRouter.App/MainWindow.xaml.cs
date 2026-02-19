using System.Windows;
using MidiRouter.UI.ViewModels;

namespace MidiRouter.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        DataContext = mainViewModel;
    }

    private void OnExitRequested(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.RequestFullExit();
            return;
        }

        System.Windows.Application.Current?.Shutdown();
    }
}
