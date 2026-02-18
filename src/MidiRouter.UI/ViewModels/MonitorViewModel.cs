using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Monitoring;
using MidiRouter.Core.Routing;

namespace MidiRouter.UI.ViewModels;

public partial class MonitorViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isPaused;

    public ObservableCollection<MidiMessageLogEntry> Entries { get; } =
    [
        new(DateTimeOffset.Now, "Loopback 1", 1, MidiMessageType.NoteOn, "NoteOn C4 Vel:92"),
        new(DateTimeOffset.Now, "Loopback 1", 1, MidiMessageType.NoteOff, "NoteOff C4 Vel:0")
    ];

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
    }
}
