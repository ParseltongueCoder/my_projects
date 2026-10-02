namespace Uof.Adapter;

/// <summary>Bound from the <c>Uof</c> configuration section (env vars: <c>Uof__ApiHost</c> etc.).</summary>
public sealed class UofOptions
{
    public const string Section = "Uof";

    public string AccessToken { get; set; } = "";

    /// <summary><c>Custom</c> (simulator), <c>Integration</c>, <c>Replay</c> or <c>Production</c>.</summary>
    public string Environment { get; set; } = "Custom";

    // Custom environment only (the simulator).
    public string ApiHost { get; set; } = "localhost:8080";
    public bool ApiUseSsl { get; set; }
    public string MessagingHost { get; set; } = "localhost";
    public int MessagingPort { get; set; } = 5671;
    public bool MessagingUseSsl { get; set; } = true;

    /// <summary>Empty on Betradar; the local RabbitMQ needs one ("sim").</summary>
    public string? MessagingPassword { get; set; }

    public int NodeId { get; set; } = 1;
    public string Language { get; set; } = "en";
}
