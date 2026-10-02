using System.Net.Security;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using UofSim.Core.Messages;

namespace UofSim.Host.Amqp;

public interface IFeedPublisher
{
    Task PublishAsync(string routingKey, byte[] body, CancellationToken ct = default);
}

/// <summary>Swallows messages; used when AMQP is disabled (REST-only runs and tests).</summary>
public sealed class NullFeedPublisher(ILogger<NullFeedPublisher> log) : IFeedPublisher
{
    public Task PublishAsync(string routingKey, byte[] body, CancellationToken ct = default)
    {
        log.LogDebug("AMQP disabled, dropping {RoutingKey}", routingKey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Publishes to the <c>unifiedfeed</c> topic exchange the same way Betradar does: one connection,
/// <c>timestamp_in_ms</c> header on every message. Outgoing bodies are XSD-validated when
/// <see cref="SimOptions.XsdDir"/> is set.
/// </summary>
public sealed class RabbitFeedPublisher(IOptions<SimOptions> options, ILogger<RabbitFeedPublisher> log)
    : IFeedPublisher, IAsyncDisposable
{
    private readonly SimOptions _opt = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly XsdValidator? _validator =
        XsdValidator.TryLoad(options.Value.XsdDir, XsdValidator.FeedSchema);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(string routingKey, byte[] body, CancellationToken ct = default)
    {
        if (_validator?.Validate(body) is { Count: > 0 } errors)
        {
            log.LogError("Outgoing {RoutingKey} fails XSD validation: {Errors}", routingKey, string.Join("; ", errors));
        }

        await _lock.WaitAsync(ct);
        try
        {
            var channel = await EnsureChannelAsync(ct);
            var props = new BasicProperties
            {
                DeliveryMode = DeliveryModes.Transient,
                Headers = new Dictionary<string, object?>
                {
                    ["timestamp_in_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            };
            await channel.BasicPublishAsync(_opt.Exchange, routingKey, mandatory: false, props, body, ct);
            log.LogDebug("Published {RoutingKey} ({Bytes} bytes)", routingKey, body.Length);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        if (_connection is not { IsOpen: true })
        {
            var factory = new ConnectionFactory
            {
                HostName = _opt.AmqpHost,
                Port = _opt.AmqpPort,
                VirtualHost = _opt.VirtualHost,
                UserName = _opt.AmqpUser,
                Password = _opt.AmqpPassword,
                AutomaticRecoveryEnabled = true,
                ClientProvidedName = "uofsim-publisher",
                Ssl = new SslOption
                {
                    Enabled = _opt.AmqpUseTls,
                    ServerName = _opt.AmqpHost,
                    // Local self-signed certificate; the simulator never talks to a real broker.
                    AcceptablePolicyErrors = SslPolicyErrors.RemoteCertificateChainErrors
                                             | SslPolicyErrors.RemoteCertificateNameMismatch,
                },
            };
            _connection = await factory.CreateConnectionAsync(ct);
            log.LogInformation("Connected to AMQP {Host}:{Port} vhost {VHost}; XSD validation {Validation}",
                _opt.AmqpHost, _opt.AmqpPort, _opt.VirtualHost, _validator is null ? "off" : "on");
        }

        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
        return _channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
        _lock.Dispose();
    }
}
