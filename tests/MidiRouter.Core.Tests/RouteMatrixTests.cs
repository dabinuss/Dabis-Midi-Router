using MidiRouter.Core.Routing;

namespace MidiRouter.Core.Tests;

public class RouteMatrixTests
{
    [Fact]
    public void ShouldRoute_ReturnsTrue_WhenRouteAndFilterAllowMessage()
    {
        var matrix = new RouteMatrix();
        var route = new RouteDefinition(
            Guid.NewGuid(),
            "loop:1",
            "loop:2",
            enabled: true,
            filter: new RouteFilter(new[] { 1 }, new[] { MidiMessageType.NoteOn }));

        matrix.AddOrUpdateRoute(route);

        var result = matrix.ShouldRoute(route.Id, channel: 1, MidiMessageType.NoteOn);

        Assert.True(result);
    }

    [Fact]
    public void ShouldRoute_ReturnsFalse_WhenChannelIsBlocked()
    {
        var matrix = new RouteMatrix();
        var route = new RouteDefinition(
            Guid.NewGuid(),
            "loop:1",
            "loop:2",
            enabled: true,
            filter: new RouteFilter(new[] { 2 }, new[] { MidiMessageType.NoteOn }));

        matrix.AddOrUpdateRoute(route);

        var result = matrix.ShouldRoute(route.Id, channel: 1, MidiMessageType.NoteOn);

        Assert.False(result);
    }
}
