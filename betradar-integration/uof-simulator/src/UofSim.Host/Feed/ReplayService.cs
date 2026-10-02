using Microsoft.Extensions.Options;
using UofSim.Core.Messages;
using UofSim.Core.Recordings;
using UofSim.Host.Amqp;

namespace UofSim.Host.Feed;

public sealed record ReplayStatus(string? File, double Speed, bool Loop, int Published, int Total, bool Running);

/// <summary>
/// L1 fidelity: publishes a JSONL recording with its original spacing (divided by speed),
/// rewriting each message's root <c>timestamp</c> to the current time.
/// </summary>
public sealed class ReplayService(
    IFeedPublisher publisher,
    TimeProvider clock,
    IOptions<SimOptions> options,
    ILogger<ReplayService> log) : BackgroundService
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private ReplayStatus _status = new(null, 1, false, 0, 0, false);
    private CancellationToken _stopping;

    public ReplayStatus Status => _status;

    /// <summary>Starts (or restarts) a replay. <paramref name="file"/> is relative to the data directory.</summary>
    public ReplayStatus Start(string file, double speed, bool loop)
    {
        if (speed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "speed must be > 0");
        }
        var path = ResolvePath(file);
        var messages = Recording.Read(path);

        lock (_gate)
        {
            _current?.Cancel();
            _current = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            _status = new ReplayStatus(file, speed, loop, 0, messages.Count, true);
            var token = _current.Token;
            _ = Task.Run(() => RunAsync(messages, speed, loop, token), token);
            return _status;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _status = _status with { Running = false };
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var onStart = options.Value.ReplayOnStart;
        if (!string.IsNullOrWhiteSpace(onStart))
        {
            Start(onStart, options.Value.ReplaySpeed, options.Value.ReplayLoop);
        }
        return Task.CompletedTask;
    }

    private async Task RunAsync(IReadOnlyList<RecordedMessage> messages, double speed, bool loop, CancellationToken ct)
    {
        try
        {
            do
            {
                var started = clock.GetTimestamp();
                for (var i = 0; i < messages.Count; i++)
                {
                    var m = messages[i];
                    var due = TimeSpan.FromMilliseconds(m.OffsetMs / speed) - clock.GetElapsedTime(started);
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due, clock, ct);
                    }
                    var body = FeedMessageBuilder.WithTimestamp(m.Body, clock.GetUtcNow().ToUnixTimeMilliseconds());
                    await publisher.PublishAsync(m.RoutingKey, body, ct);
                    _status = _status with { Published = i + 1 };
                }
                log.LogInformation("Replay of {File} finished ({Count} messages)", _status.File, messages.Count);
            }
            while (loop && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Replay of {File} failed", _status.File);
        }
        finally
        {
            lock (_gate)
            {
                // A newer replay may already own the status; only the current run may mark it finished.
                if (_current?.Token == ct)
                {
                    _status = _status with { Running = false };
                }
            }
        }
    }

    private string ResolvePath(string file)
    {
        var dataDir = Path.GetFullPath(options.Value.DataDir);
        var path = Path.GetFullPath(Path.Combine(dataDir, file));
        if (!path.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Recording must be inside the data directory", nameof(file));
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Recording '{file}' not found", path);
        }
        return path;
    }
}
