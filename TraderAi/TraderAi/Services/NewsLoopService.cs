using Microsoft.Extensions.Options;

namespace TraderAi.Services;

// Background loop that publishes random news on a fixed interval, independent of the cycle loop. Like the
// market loop it is gated by an Enabled kill-switch and, at runtime, by the market being Running, so it
// idles until the simulation is started. Each tick runs in its own DI scope and goes through the shared
// cycle lock, so a publish never overlaps a cycle tick.
public sealed class NewsLoopService(
    IServiceScopeFactory scopeFactory,
    IOptions<NewsLoopOptions> options,
    ILogger<NewsLoopService> logger) : BackgroundService
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
                    var newsService = scope.ServiceProvider.GetRequiredService<NewsService>();
                    var result = await newsService.PublishRandomNewsAsync();

                    if (result.Published)
                    {
                        logger.LogInformation(
                            "Published news \"{Title}\" with {Scope} impact moving {Count} companies.",
                            result.Post!.Title,
                            result.Post.Scope,
                            result.CompaniesMoved);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "News publish tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }
}
