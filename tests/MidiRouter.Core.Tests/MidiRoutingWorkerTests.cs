using MidiRouter.Core.Engine;
using MidiRouter.Core.Monitoring;
using MidiRouter.Core.Routing;

namespace MidiRouter.Core.Tests;

public class MidiRoutingWorkerTests
{
    [Fact]
    public async Task RoutesMatchingPacketToConfiguredTarget()
    {
        var matrix = new RouteMatrix();
        var route = new RouteDefinition(
            Guid.NewGuid(),
            "hw:in1",
            "hw:out1",
            enabled: true,
            filter: new RouteFilter([1], [MidiMessageType.NoteOn]));
        matrix.AddOrUpdateRoute(route);

        var endpointCatalog = new InMemoryMidiEndpointCatalog();
        endpointCatalog.ReplaceEndpoints(
        [
            new MidiEndpointDescriptor("hw:in1", "Input", MidiEndpointKind.Hardware, true, false),
            new MidiEndpointDescriptor("hw:out1", "Output", MidiEndpointKind.Hardware, false, true)
        ]);

        var session = new InMemoryMidiSession(endpointCatalog);
        var analyzer = new TrafficAnalyzer();
        var log = new RingBufferMidiMessageLog();
        await using var worker = new MidiRoutingWorker(matrix, session, endpointCatalog, analyzer, log);

        worker.Start();
        await session.StartAsync();

        session.InjectIncomingPacket(new MidiPacket(
            "hw:in1",
            [0x90, 60, 100],
            Channel: 1,
            MidiMessageType.NoteOn,
            DateTimeOffset.UtcNow));

        await Task.Delay(100);
        var sentPackets = session.GetSentPackets();

        Assert.Single(sentPackets);
        Assert.Equal("hw:out1", sentPackets[0].TargetEndpointId);
    }

    [Fact]
    public async Task DoesNotRouteWhenFilterBlocksPacket()
    {
        var matrix = new RouteMatrix();
        var route = new RouteDefinition(
            Guid.NewGuid(),
            "hw:in1",
            "hw:out1",
            enabled: true,
            filter: new RouteFilter([2], [MidiMessageType.ControlChange]));
        matrix.AddOrUpdateRoute(route);

        var endpointCatalog = new InMemoryMidiEndpointCatalog();
        endpointCatalog.ReplaceEndpoints(
        [
            new MidiEndpointDescriptor("hw:in1", "Input", MidiEndpointKind.Hardware, true, false),
            new MidiEndpointDescriptor("hw:out1", "Output", MidiEndpointKind.Hardware, false, true)
        ]);

        var session = new InMemoryMidiSession(endpointCatalog);
        var analyzer = new TrafficAnalyzer();
        var log = new RingBufferMidiMessageLog();
        await using var worker = new MidiRoutingWorker(matrix, session, endpointCatalog, analyzer, log);

        worker.Start();
        await session.StartAsync();

        session.InjectIncomingPacket(new MidiPacket(
            "hw:in1",
            [0x90, 60, 100],
            Channel: 1,
            MidiMessageType.NoteOn,
            DateTimeOffset.UtcNow));

        await Task.Delay(100);
        var sentPackets = session.GetSentPackets();

        Assert.Empty(sentPackets);
    }

    [Fact]
    public async Task AppliesRouteChangesWithoutRestart()
    {
        var matrix = new RouteMatrix();
        var endpointCatalog = new InMemoryMidiEndpointCatalog();
        endpointCatalog.ReplaceEndpoints(
        [
            new MidiEndpointDescriptor("hw:in1", "Input", MidiEndpointKind.Hardware, true, false),
            new MidiEndpointDescriptor("hw:out1", "Output 1", MidiEndpointKind.Hardware, false, true),
            new MidiEndpointDescriptor("hw:out2", "Output 2", MidiEndpointKind.Hardware, false, true)
        ]);

        var firstRoute = new RouteDefinition(
            Guid.NewGuid(),
            "hw:in1",
            "hw:out1",
            enabled: true,
            filter: RouteFilter.AllowAll);
        matrix.AddOrUpdateRoute(firstRoute);

        var session = new InMemoryMidiSession(endpointCatalog);
        var analyzer = new TrafficAnalyzer();
        var log = new RingBufferMidiMessageLog();
        await using var worker = new MidiRoutingWorker(matrix, session, endpointCatalog, analyzer, log);

        worker.Start();
        await session.StartAsync();

        session.InjectIncomingPacket(new MidiPacket(
            "hw:in1",
            [0x90, 60, 100],
            Channel: 1,
            MidiMessageType.NoteOn,
            DateTimeOffset.UtcNow));

        await Task.Delay(80);

        matrix.ReplaceRoutes(
        [
            new RouteDefinition(
                Guid.NewGuid(),
                "hw:in1",
                "hw:out2",
                enabled: true,
                filter: RouteFilter.AllowAll)
        ]);

        session.InjectIncomingPacket(new MidiPacket(
            "hw:in1",
            [0x90, 61, 100],
            Channel: 1,
            MidiMessageType.NoteOn,
            DateTimeOffset.UtcNow));

        await Task.Delay(80);
        var sentPackets = session.GetSentPackets();

        Assert.Equal(2, sentPackets.Count);
        Assert.Equal("hw:out1", sentPackets[0].TargetEndpointId);
        Assert.Equal("hw:out2", sentPackets[1].TargetEndpointId);
    }
}
