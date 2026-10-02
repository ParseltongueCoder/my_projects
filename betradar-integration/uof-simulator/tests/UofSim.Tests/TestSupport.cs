using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UofSim.Host.Amqp;

namespace UofSim.Tests;

public sealed record Published(string RoutingKey, string Body);

/// <summary>Captures everything the simulator would publish to RabbitMQ.</summary>
public sealed class CapturingPublisher : IFeedPublisher
{
    public ConcurrentQueue<Published> Messages { get; } = new();

    public Task PublishAsync(string routingKey, byte[] body, CancellationToken ct = default)
    {
        Messages.Enqueue(new Published(routingKey, Encoding.UTF8.GetString(body)));
        return Task.CompletedTask;
    }

    public async Task<Published> WaitForAsync(Func<Published, bool> match, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (Messages.FirstOrDefault(match) is { } found)
            {
                return found;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Expected message was not published");
    }
}

/// <summary>Simulator host without RabbitMQ: publishing goes to <see cref="Publisher"/>.</summary>
public sealed class SimFactory : WebApplicationFactory<Program>
{
    public CapturingPublisher Publisher { get; } = new();

    public string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Sim:AmqpEnabled", "false");
        builder.UseSetting("Sim:RecoveryDelayMs", "0");
        builder.UseSetting("Sim:AliveIntervalSeconds", "3600");
        builder.UseSetting("Sim:DataDir", DataDir);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFeedPublisher>();
            services.AddSingleton<IFeedPublisher>(Publisher);
        });
    }

    public HttpClient CreateApiClient(string? token = "sim-token-0000000000")
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add("x-access-token", token);
        }
        return client;
    }
}

/// <summary>Runs only when UOF_XSD_DIR points at a local copy of the Sportradar XSDs (never committed).</summary>
public sealed class XsdFactAttribute : FactAttribute
{
    public static string? XsdDir => Environment.GetEnvironmentVariable("UOF_XSD_DIR");

    public XsdFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(XsdDir))
        {
            Skip = "UOF_XSD_DIR not set";
        }
    }
}
