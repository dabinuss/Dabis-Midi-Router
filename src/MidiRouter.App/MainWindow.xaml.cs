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
}
