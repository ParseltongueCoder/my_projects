namespace UofSim.Host;

/// <summary>Bound from the <c>Sim</c> configuration section (env vars: <c>Sim__AmqpHost</c> etc.).</summary>
public sealed class SimOptions
{
    public const string Section = "Sim";

    public int BookmakerId { get; set; } = 99999;

    /// <summary>Token clients must send in <c>x-access-token</c>; also the AMQP username of the consumer.</summary>
    public string AccessToken { get; set; } = "sim-token-0000000000";

    /// <summary>When false nothing is published (REST-only mode, used by tests).</summary>
    public bool AmqpEnabled { get; set; } = true;
    public string AmqpHost { get; set; } = "localhost";
    public int AmqpPort { get; set; } = 5671;
    public bool AmqpUseTls { get; set; } = true;
    public string AmqpUser { get; set; } = "uofsim-publisher";
    public string AmqpPassword { get; set; } = "publisher";
    public string Exchange { get; set; } = "unifiedfeed";

    public string VirtualHost => $"/unifiedfeed/{BookmakerId}";

    public int AliveIntervalSeconds { get; set; } = 10;

    /// <summary>Delay between an accepted recovery request and its snapshot_complete.</summary>
    public int RecoveryDelayMs { get; set; } = 1000;

    /// <summary>Recording to replay on startup (path relative to the data directory), empty = none.</summary>
    public string ReplayOnStart { get; set; } = "";
    public double ReplaySpeed { get; set; } = 1.0;
    public bool ReplayLoop { get; set; }

    /// <summary>Local directory with the Sportradar XSDs; empty disables outgoing message validation.</summary>
    public string XsdDir { get; set; } = "";

    public string DataDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "data");
}
