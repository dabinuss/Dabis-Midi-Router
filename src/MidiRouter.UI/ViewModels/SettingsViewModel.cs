using CommunityToolkit.Mvvm.ComponentModel;

namespace MidiRouter.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private int _logBufferSize = 5000;
}
