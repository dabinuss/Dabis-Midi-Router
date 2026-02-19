using CommunityToolkit.Mvvm.ComponentModel;
using MidiRouter.Core.Config;

namespace MidiRouter.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private int _logBufferSize = 5000;

    [ObservableProperty]
    private bool _runInBackground = true;

    [ObservableProperty]
    private bool _autoStartHost;

    public HostingMode HostingMode
    {
        get => RunInBackground ? HostingMode.TrayHost : HostingMode.InProcess;
        set => RunInBackground = value == HostingMode.TrayHost;
    }

    partial void OnLogBufferSizeChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 200_000);
        if (clamped != value)
        {
            LogBufferSize = clamped;
        }
    }

    partial void OnRunInBackgroundChanged(bool value)
    {
        OnPropertyChanged(nameof(HostingMode));
    }
}
