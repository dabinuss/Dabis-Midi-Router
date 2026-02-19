using Microsoft.Extensions.Hosting;

namespace MidiRouter.Core.Engine;

public sealed class MidiRuntimeHostedService(
    IMidiEndpointCatalog endpointCatalog,
    IMidiSession midiSession,
    MidiRoutingWorker routingWorker) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await endpointCatalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
        routingWorker.Start();
        await midiSession.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await routingWorker.StopAsync(cancellationToken).ConfigureAwait(false);
        await midiSession.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
