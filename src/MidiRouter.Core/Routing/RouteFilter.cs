namespace MidiRouter.Core.Routing;

public sealed class RouteFilter
{
    public static RouteFilter AllowAll { get; } = new();

    private readonly HashSet<int> _channels;
    private readonly HashSet<MidiMessageType> _messageTypes;

    public RouteFilter(IEnumerable<int>? channels = null, IEnumerable<MidiMessageType>? messageTypes = null)
    {
        _channels = channels?
            .Where(channel => channel is >= 1 and <= 16)
            .Distinct()
            .ToHashSet() ?? [];

        _messageTypes = messageTypes?
            .Distinct()
            .ToHashSet() ?? [];
    }

    public IReadOnlyCollection<int> Channels => _channels;

    public IReadOnlyCollection<MidiMessageType> MessageTypes => _messageTypes;

    public bool Allows(int channel, MidiMessageType messageType)
    {
        var channelAllowed = _channels.Count == 0 || _channels.Contains(channel);
        var messageTypeAllowed = _messageTypes.Count == 0 || _messageTypes.Contains(messageType);
        return channelAllowed && messageTypeAllowed;
    }
}
