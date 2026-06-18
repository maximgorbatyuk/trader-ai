using Microsoft.Extensions.Options;

namespace TraderAi.Services;

// Opt-in background loop that advances market cycles on a fixed interval. Each tick runs in its own DI
// scope and goes through MarketService's lock, so it never overlaps a manual trigger.
public sealed class MarketLoopService(
    IServiceScopeFactory scopeFactory,
    IOptions<MarketLoopOptions> options,
    ILogger<MarketLoopService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var marketService = scope.ServiceProvider.GetRequiredService<MarketService>();
                    var result = await marketService.RunCycleTickAsync();

                    if (result.Ran)
                    {
                        logger.LogInformation(
                            "Auto cycle {Cycle} completed with {Orders} orders placed and {Fills} fills.",
                            result.CompletedCycleNumber,
                            result.OrdersPlaced,
                            result.FillCount);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Auto cycle tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }
}
