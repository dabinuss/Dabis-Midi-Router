using MidiRouter.Core.Engine;

namespace MidiRouter.Core.Tests;

public class InMemoryMidiEndpointCatalogTests
{
    [Fact]
    public async Task RenameLoopbackEndpointAsync_UpdatesEndpointName()
    {
        var catalog = new InMemoryMidiEndpointCatalog();
        var loopback = await catalog.CreateLoopbackEndpointAsync("Before");

        var renamed = await catalog.RenameLoopbackEndpointAsync(loopback.Id, "After");
        var updated = catalog.GetEndpoints().First(endpoint => endpoint.Id == loopback.Id);

        Assert.True(renamed);
        Assert.Equal("After", updated.Name);
    }
}
