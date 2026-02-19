using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Monitoring;

namespace MidiRouter.UI.ViewModels;

public partial class MonitorViewModel : ObservableObject
{
    private readonly IMidiMessageLog _messageLog;
    private readonly TrafficAnalyzer _trafficAnalyzer;
    private readonly DispatcherTimer _snapshotTimer;

    [ObservableProperty]
    private bool _isPaused;

    public MonitorViewModel(IMidiMessageLog messageLog, TrafficAnalyzer trafficAnalyzer)
    {
        _messageLog = messageLog;
        _trafficAnalyzer = trafficAnalyzer;

        ReplaceEntries(_messageLog.GetEntries());

        _messageLog.EntryAdded += OnEntryAdded;
        _messageLog.Cleared += OnLogCleared;

        _snapshotTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _snapshotTimer.Tick += (_, _) => RefreshSnapshots();
        _snapshotTimer.Start();

        RefreshSnapshots();
    }

    public ObservableCollection<MidiMessageLogEntry> Entries { get; } = [];

    public ObservableCollection<TrafficSnapshot> TrafficSnapshots { get; } = [];

    [RelayCommand]
    private void Clear()
    {
        _messageLog.Clear();
    }

    private void OnEntryAdded(object? sender, MidiMessageLogEntry entry)
    {
        if (IsPaused)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            Entries.Add(entry);
            TrimEntriesToCapacity();
        });
    }

    private void OnLogCleared(object? sender, EventArgs args)
    {
        RunOnUiThread(() => Entries.Clear());
    }

    private void RefreshSnapshots()
    {
        var snapshots = _trafficAnalyzer.GetAllSnapshots();
        RunOnUiThread(() =>
        {
            TrafficSnapshots.Clear();
            foreach (var snapshot in snapshots)
            {
                TrafficSnapshots.Add(snapshot);
            }
        });
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            RunOnUiThread(() => ReplaceEntries(_messageLog.GetEntries()));
        }
    }

    private void ReplaceEntries(IReadOnlyList<MidiMessageLogEntry> entries)
    {
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        TrimEntriesToCapacity();
    }

    private void TrimEntriesToCapacity()
    {
        var capacity = Math.Max(1, _messageLog.Capacity);
        while (Entries.Count > capacity)
        {
            Entries.RemoveAt(0);
        }
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action, DispatcherPriority.Background);
    }
}
