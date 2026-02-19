using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MidiRouter.Core.Engine;
using MidiRouter.Core.Monitoring;
using MidiRouter.Core.Routing;

namespace MidiRouter.UI.ViewModels;

public partial class MonitorViewModel : ObservableObject
{
    private readonly IMidiMessageLog _messageLog;
    private readonly TrafficAnalyzer _trafficAnalyzer;
    private readonly IMidiEndpointCatalog _endpointCatalog;
    private readonly DispatcherTimer _snapshotTimer;
    private readonly DispatcherTimer _flushTimer;
    private readonly object _entriesSync = new();
    private readonly List<MidiMessageLogEntry> _allEntries = [];
    private readonly Queue<MidiMessageLogEntry> _pendingEntries = [];

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private EndpointFilterOption? _selectedEndpointFilter;

    [ObservableProperty]
    private ChannelFilterOption? _selectedChannelFilter;

    [ObservableProperty]
    private MessageTypeFilterOption? _selectedMessageTypeFilter;

    public MonitorViewModel(
        IMidiMessageLog messageLog,
        TrafficAnalyzer trafficAnalyzer,
        IMidiEndpointCatalog endpointCatalog)
    {
        _messageLog = messageLog;
        _trafficAnalyzer = trafficAnalyzer;
        _endpointCatalog = endpointCatalog;

        ChannelFilters.Add(new ChannelFilterOption(null, "Alle Kanaele"));
        for (var channel = 1; channel <= 16; channel++)
        {
            ChannelFilters.Add(new ChannelFilterOption(channel, $"Kanal {channel}"));
        }

        MessageTypeFilters.Add(new MessageTypeFilterOption(null, "Alle Typen"));
        foreach (var messageType in Enum.GetValues<MidiMessageType>())
        {
            MessageTypeFilters.Add(new MessageTypeFilterOption(messageType, messageType.ToString()));
        }

        SelectedChannelFilter = ChannelFilters[0];
        SelectedMessageTypeFilter = MessageTypeFilters[0];

        ReplaceAllEntries(_messageLog.GetEntries());
        RefreshEndpointFilterOptions();
        SelectedEndpointFilter ??= EndpointFilters.FirstOrDefault();
        RebuildFilteredEntries();

        _messageLog.EntryAdded += OnEntryAdded;
        _messageLog.Cleared += OnLogCleared;
        _endpointCatalog.EndpointsChanged += (_, _) => RunOnUiThread(RefreshEndpointFilterOptions);

        _snapshotTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _snapshotTimer.Tick += (_, _) => RefreshSnapshots();
        _snapshotTimer.Start();

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _flushTimer.Tick += (_, _) => FlushPendingEntries();
        _flushTimer.Start();

        RefreshSnapshots();
    }

    public ObservableCollection<MidiMessageLogRow> Entries { get; } = [];

    public ObservableCollection<TrafficSnapshot> TrafficSnapshots { get; } = [];

    public ObservableCollection<EndpointFilterOption> EndpointFilters { get; } = [];

    public ObservableCollection<ChannelFilterOption> ChannelFilters { get; } = [];

    public ObservableCollection<MessageTypeFilterOption> MessageTypeFilters { get; } = [];

    [RelayCommand]
    private void Clear()
    {
        _messageLog.Clear();
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var saveDialog = new SaveFileDialog
        {
            Filter = "CSV-Datei (*.csv)|*.csv",
            FileName = $"midi-monitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };

        if (saveDialog.ShowDialog() != true)
        {
            return;
        }

        List<MidiMessageLogRow> rows;
        lock (_entriesSync)
        {
            rows = Entries.ToList();
        }

        var builder = new StringBuilder();
        builder.AppendLine("Timestamp,Endpoint,Channel,Type,Detail");
        foreach (var row in rows)
        {
            builder.Append(EscapeCsv(row.Timestamp.ToString("O", CultureInfo.InvariantCulture)));
            builder.Append(',');
            builder.Append(EscapeCsv(row.EndpointName));
            builder.Append(',');
            builder.Append(row.Channel);
            builder.Append(',');
            builder.Append(EscapeCsv(row.MessageType.ToString()));
            builder.Append(',');
            builder.Append(EscapeCsv(row.Detail));
            builder.AppendLine();
        }

        await File.WriteAllTextAsync(saveDialog.FileName, builder.ToString(), Encoding.UTF8);
    }

    partial void OnSelectedEndpointFilterChanged(EndpointFilterOption? value)
    {
        RebuildFilteredEntries();
    }

    partial void OnSelectedChannelFilterChanged(ChannelFilterOption? value)
    {
        RebuildFilteredEntries();
    }

    partial void OnSelectedMessageTypeFilterChanged(MessageTypeFilterOption? value)
    {
        RebuildFilteredEntries();
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            RebuildFilteredEntries();
        }
    }

    private void OnEntryAdded(object? sender, MidiMessageLogEntry entry)
    {
        lock (_entriesSync)
        {
            _allEntries.Add(entry);
            TrimAllEntriesToCapacity();
            _pendingEntries.Enqueue(entry);
        }
    }

    private void OnLogCleared(object? sender, EventArgs args)
    {
        lock (_entriesSync)
        {
            _allEntries.Clear();
            _pendingEntries.Clear();
        }

        RunOnUiThread(() =>
        {
            Entries.Clear();
            RefreshEndpointFilterOptions();
        });
    }

    private void FlushPendingEntries()
    {
        if (IsPaused)
        {
            return;
        }

        List<MidiMessageLogEntry> pending;
        lock (_entriesSync)
        {
            if (_pendingEntries.Count == 0)
            {
                return;
            }

            pending = [];
            var maxBatch = 400;
            while (_pendingEntries.Count > 0 && pending.Count < maxBatch)
            {
                pending.Add(_pendingEntries.Dequeue());
            }
        }

        RunOnUiThread(() =>
        {
            foreach (var entry in pending)
            {
                if (!MatchesFilter(entry))
                {
                    continue;
                }

                Entries.Add(ToRow(entry));
            }

            TrimVisibleEntries();
        });
    }

    private void RebuildFilteredEntries()
    {
        List<MidiMessageLogEntry> snapshot;
        lock (_entriesSync)
        {
            snapshot = _allEntries.ToList();
            _pendingEntries.Clear();
        }

        RunOnUiThread(() =>
        {
            Entries.Clear();
            foreach (var entry in snapshot)
            {
                if (MatchesFilter(entry))
                {
                    Entries.Add(ToRow(entry));
                }
            }

            TrimVisibleEntries();
            RefreshEndpointFilterOptions();
        });
    }

    private void ReplaceAllEntries(IReadOnlyList<MidiMessageLogEntry> entries)
    {
        lock (_entriesSync)
        {
            _allEntries.Clear();
            _allEntries.AddRange(entries);
            TrimAllEntriesToCapacity();
            _pendingEntries.Clear();
        }
    }

    private void RefreshSnapshots()
    {
        var snapshots = _trafficAnalyzer.PeekAllSnapshots();
        RunOnUiThread(() =>
        {
            TrafficSnapshots.Clear();
            foreach (var snapshot in snapshots)
            {
                TrafficSnapshots.Add(snapshot);
            }
        });
    }

    private void RefreshEndpointFilterOptions()
    {
        var selectedId = SelectedEndpointFilter?.EndpointName;
        var endpointNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in _endpointCatalog.GetEndpoints())
        {
            endpointNames.Add(endpoint.Name);
        }

        lock (_entriesSync)
        {
            foreach (var entry in _allEntries)
            {
                endpointNames.Add(entry.EndpointName);
            }
        }

        EndpointFilters.Clear();
        EndpointFilters.Add(new EndpointFilterOption(null, "Alle Endpoints"));
        foreach (var endpointName in endpointNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            EndpointFilters.Add(new EndpointFilterOption(endpointName, endpointName));
        }

        SelectedEndpointFilter = EndpointFilters.FirstOrDefault(option =>
            string.Equals(option.EndpointName, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? EndpointFilters.FirstOrDefault();
    }

    private bool MatchesFilter(MidiMessageLogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(SelectedEndpointFilter?.EndpointName) &&
            !string.Equals(entry.EndpointName, SelectedEndpointFilter.EndpointName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SelectedChannelFilter?.Channel is int channel && entry.Channel != channel)
        {
            return false;
        }

        if (SelectedMessageTypeFilter?.MessageType is MidiMessageType messageType && entry.MessageType != messageType)
        {
            return false;
        }

        return true;
    }

    private void TrimAllEntriesToCapacity()
    {
        var capacity = Math.Max(1, _messageLog.Capacity);
        if (_allEntries.Count <= capacity)
        {
            return;
        }

        _allEntries.RemoveRange(0, _allEntries.Count - capacity);
    }

    private void TrimVisibleEntries()
    {
        var capacity = Math.Max(1, _messageLog.Capacity);
        while (Entries.Count > capacity)
        {
            Entries.RemoveAt(0);
        }
    }

    private static MidiMessageLogRow ToRow(MidiMessageLogEntry entry)
    {
        return new MidiMessageLogRow(
            entry.Timestamp,
            entry.EndpointName,
            entry.Channel,
            entry.MessageType,
            entry.Detail,
            GetTypeBrush(entry.MessageType));
    }

    private static Brush GetTypeBrush(MidiMessageType type)
    {
        return type switch
        {
            MidiMessageType.NoteOn => Brushes.ForestGreen,
            MidiMessageType.NoteOff => Brushes.SeaGreen,
            MidiMessageType.ControlChange => Brushes.DodgerBlue,
            MidiMessageType.ProgramChange => Brushes.DarkOrchid,
            MidiMessageType.PitchBend => Brushes.DarkOrange,
            MidiMessageType.SysEx => Brushes.SteelBlue,
            MidiMessageType.Clock => Brushes.DimGray,
            _ => Brushes.Black
        };
    }

    private static string EscapeCsv(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
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

    public sealed record EndpointFilterOption(string? EndpointName, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record ChannelFilterOption(int? Channel, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record MessageTypeFilterOption(MidiMessageType? MessageType, string Label)
    {
        public override string ToString() => Label;
    }
}

public sealed record MidiMessageLogRow(
    DateTimeOffset Timestamp,
    string EndpointName,
    int Channel,
    MidiMessageType MessageType,
    string Detail,
    Brush TypeBrush);
