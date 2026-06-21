namespace TraderAi.Services;

// Process-wide gate that serializes cycle execution and order placement so the background loop and
// manual API triggers never mutate the market at the same time. Registered as a singleton.
public sealed class MarketCycleLock
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
