namespace MidiRouter.Core.Monitoring;

public sealed record TrafficSnapshot(
    string EndpointId,
    double MessagesPerSecond,
    double BytesPerSecond,
    IReadOnlyCollection<int> ActiveChannels,
    DateTimeOffset CapturedAtUtc);
