using GrainWire.Server.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrainWire.Server;

internal sealed class LeaseRenewalService(Gateway gateway, GrainWireGatewayOptions options, ILogger<LeaseRenewalService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.LeaseRenewInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await gateway.RenewLeasesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Lease renewal failed.");
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            log.LogError(ex, "Lease renewal canceled.");
        }
    }
}
