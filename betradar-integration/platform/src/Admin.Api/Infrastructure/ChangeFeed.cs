using System.Collections.Concurrent;
using System.Threading.Channels;
using Npgsql;

namespace Admin.Api.Infrastructure;

/// <summary>
/// LISTENs on PostgreSQL channel <c>sb_changes</c> (V004 triggers) and fans every notification out to
/// the connected SSE clients. Slow clients drop the oldest notifications instead of blocking others:
/// the browser treats a change as "refresh", so losing intermediate ones is harmless.
/// </summary>
public sealed class ChangeFeed(NpgsqlDataSource db, ILogger<ChangeFeed> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public int Subscribers => _subscribers.Count;

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, e) =>
                {
                    foreach (var subscriber in _subscribers.Values)
                    {
                        subscriber.Writer.TryWrite(e.Payload);
                    }
                };
                await using (var listen = new NpgsqlCommand("LISTEN sb_changes", conn))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }
                log.LogInformation("Listening for canonical model changes");
                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Change listener lost its connection; retrying in 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
