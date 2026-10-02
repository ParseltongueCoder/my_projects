using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Canonical.Store;
using Sportradar.OddsFeed.SDK.Api;
using Sportradar.OddsFeed.SDK.Api.Config;
using Sportradar.OddsFeed.SDK.Common.Enums;
using Sportradar.OddsFeed.SDK.Common.Extensions;
using Uof.Adapter;

var builder = Host.CreateApplicationBuilder(args);

var uof = builder.Configuration.GetSection(UofOptions.Section).Get<UofOptions>() ?? new UofOptions();
builder.Services.Configure<UofOptions>(builder.Configuration.GetSection(UofOptions.Section));
builder.Services.AddUofSdk(BuildSdkConfiguration(uof));

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Platform")
    ?? throw new InvalidOperationException("ConnectionStrings:Platform is not configured")));
builder.Services.AddSingleton(sp => new CanonicalStore(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddHostedService<UofFeedService>();

builder.Build().Run();

// Simulator → Integration → Production is a configuration change only (docs/03 §9).
static IUofConfiguration BuildSdkConfiguration(UofOptions o)
{
    var token = UofSdk.GetConfigurationBuilder().SetAccessToken(o.AccessToken);
    var culture = CultureInfo.GetCultureInfo(o.Language);

    if (string.Equals(o.Environment, "Custom", StringComparison.OrdinalIgnoreCase))
    {
        var custom = token.SelectCustom()
            .SetApiHost(o.ApiHost)
            .UseApiSsl(o.ApiUseSsl)
            .SetMessagingHost(o.MessagingHost)
            .SetMessagingPort(o.MessagingPort)
            .UseMessagingSsl(o.MessagingUseSsl)
            .SetMessagingUsername(o.AccessToken)
            .SetNodeId(o.NodeId)
            .SetDefaultLanguage(culture);
        if (!string.IsNullOrEmpty(o.MessagingPassword))
        {
            custom = custom.SetMessagingPassword(o.MessagingPassword);
        }
        return custom.Build();
    }

    if (string.Equals(o.Environment, "Replay", StringComparison.OrdinalIgnoreCase))
    {
        return token.SelectReplay().SetNodeId(o.NodeId).SetDefaultLanguage(culture).Build();
    }

    var environment = Enum.Parse<SdkEnvironment>(o.Environment, ignoreCase: true);
    return token.SelectEnvironment(environment).SetNodeId(o.NodeId).SetDefaultLanguage(culture).Build();
}
