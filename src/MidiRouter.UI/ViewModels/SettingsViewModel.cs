using CommunityToolkit.Mvvm.ComponentModel;

namespace MidiRouter.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private int _logBufferSize = 5000;

    partial void OnLogBufferSizeChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 200_000);
        if (clamped != value)
        {
            LogBufferSize = clamped;
        }
    }
}
