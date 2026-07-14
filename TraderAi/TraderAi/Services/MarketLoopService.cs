using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace TraderAi.Services;

// Background loop that advances market cycles on a fixed interval. The Enabled flag is a master
// kill-switch; whether a tick actually does anything is gated at runtime by the market status, so the
// loop idles until the market is started. Each tick runs in its own DI scope and goes through
// MarketService's lock, so it never overlaps another market mutation.
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

                    var stopwatch = Stopwatch.StartNew();
                    var result = await marketService.RunCycleTickAsync();
                    stopwatch.Stop();

                    if (result.Ran)
                    {
                        logger.LogInformation(
                            "Auto cycle {Cycle} completed in {ElapsedSeconds:0.00}s with {Orders} orders placed and {Fills} fills.",
                            result.CompletedCycleNumber,
                            stopwatch.Elapsed.TotalSeconds,
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
