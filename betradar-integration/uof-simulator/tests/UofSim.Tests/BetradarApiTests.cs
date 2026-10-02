using System.Net;
using System.Xml.Linq;

namespace UofSim.Tests;

public class BetradarApiTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private static async Task<XElement> XmlAsync(HttpResponseMessage response) =>
        XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;

    [Fact]
    public async Task Requests_without_valid_token_are_forbidden()
    {
        var noToken = await factory.CreateApiClient(token: null).GetAsync("/v1/users/whoami.xml");
        var wrongToken = await factory.CreateApiClient("nope").GetAsync("/v1/users/whoami.xml");

        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);
        Assert.Equal("FORBIDDEN", (string?)(await XmlAsync(noToken)).Attribute("response_code"));
    }

    [Fact]
    public async Task Whoami_returns_bookmaker_and_virtual_host()
    {
        var response = await factory.CreateApiClient().GetAsync("/v1/users/whoami.xml");
        var root = await XmlAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("bookmaker_details", root.Name.LocalName);
        Assert.Equal("99999", (string?)root.Attribute("bookmaker_id"));
        Assert.Equal("/unifiedfeed/99999", (string?)root.Attribute("virtual_host"));
    }

    [Fact]
    public async Task Producer_api_urls_point_back_to_the_simulator_and_end_with_slash()
    {
        var root = await XmlAsync(await factory.CreateApiClient().GetAsync("/v1/descriptions/producers.xml"));
        var producers = root.Elements("producer").ToList();

        Assert.Equal(["1", "3"], producers.Select(p => (string)p.Attribute("id")!));
        Assert.All(producers, p =>
        {
            var apiUrl = (string)p.Attribute("api_url")!;
            Assert.StartsWith("http://localhost/v1/", apiUrl);
            Assert.EndsWith("/", apiUrl);
        });
    }

    [Theory]
    [InlineData("/v1/descriptions/en/markets.xml?include_mappings=true", "market_descriptions")]
    [InlineData("/v1/descriptions/de/markets.xml", "market_descriptions")]
    [InlineData("/v1/descriptions/en/variants.xml?include_mappings=true", "variant_descriptions")]
    [InlineData("/v1/descriptions/en/match_status.xml", "match_status_descriptions")]
    [InlineData("/v1/descriptions/betstop_reasons.xml", "betstop_reasons_descriptions")]
    [InlineData("/v1/descriptions/betting_status.xml", "betting_status_descriptions")]
    [InlineData("/v1/descriptions/void_reasons.xml", "void_reasons_descriptions")]
    public async Task Description_endpoints_serve_xml(string path, string rootName)
    {
        var response = await factory.CreateApiClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rootName, (await XmlAsync(response)).Name.LocalName);
    }

    [Fact]
    public async Task Language_segment_cannot_escape_the_descriptions_folder()
    {
        var response = await factory.CreateApiClient().GetAsync("/v1/descriptions/..%2F..%2Fappsettings/markets.xml");
        Assert.Equal("market_descriptions", (await XmlAsync(response)).Name.LocalName);
    }

    [Fact]
    public async Task Recovery_request_is_accepted_and_answered_with_snapshot_complete()
    {
        var response = await factory.CreateApiClient()
            .PostAsync("/v1/liveodds/recovery/initiate_request?after=1700000000000&request_id=4242&node_id=1", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var published = await factory.Publisher.WaitForAsync(m => m.Body.Contains("request_id=\"4242\""));
        Assert.Equal("-.-.-.snapshot_complete.-.-.-.1", published.RoutingKey);
        Assert.Contains("product=\"1\"", published.Body);
    }

    [Fact]
    public async Task Event_recovery_is_accepted()
    {
        var response = await factory.CreateApiClient()
            .PostAsync("/v1/pre/odds/events/sr:match:900000001/initiate_request?request_id=7", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/liveodds/recovery/initiate_request", HttpStatusCode.BadRequest)]
    [InlineData("/v1/unknown/recovery/initiate_request?request_id=1", HttpStatusCode.NotFound)]
    public async Task Invalid_recovery_requests_are_rejected(string path, HttpStatusCode expected)
    {
        var response = await factory.CreateApiClient().PostAsync(path, null);
        Assert.Equal(expected, response.StatusCode);
    }
}
