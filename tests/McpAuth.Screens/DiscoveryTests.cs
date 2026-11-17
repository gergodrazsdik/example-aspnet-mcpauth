using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace McpAuth.Screens;

/// <summary>What a client sees before it has a token: the challenge and the metadata it points to.</summary>
public class DiscoveryTests
{
    [Before(Test)]
    public async Task RequireStack() => await Stack.RequireRunningAsync();

    [Test]
    public async Task Without_a_token_the_server_points_to_its_metadata()
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(Stack.Mcp, JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Headers.WwwAuthenticate.ToString())
            .IsEqualTo("Bearer resource_metadata=\"http://localhost:5100/.well-known/oauth-protected-resource/mcp\", scope=\"notes:read notes:write\"");
    }

    [Test]
    public async Task A_rejected_token_gets_invalid_token()
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Stack.Mcp) { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        request.Headers.Authorization = new("Bearer", "not.a.token");
        using var response = await http.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Headers.WwwAuthenticate.ToString()).EndsWith(", error=\"invalid_token\"");
    }

    [Test]
    public async Task The_metadata_names_the_resource_Keycloak_and_the_scopes()
    {
        using var http = new HttpClient();
        var metadata = await http.GetFromJsonAsync<JsonElement>("http://localhost:5100/.well-known/oauth-protected-resource/mcp");

        await Assert.That(metadata.GetProperty("resource").GetString()).IsEqualTo(Stack.Mcp);
        await Assert.That(metadata.GetProperty("authorization_servers")[0].GetString()).IsEqualTo(Stack.Realm);
        await Assert.That(metadata.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString() ?? ""))
            .IsEquivalentTo(["notes:read", "notes:write"]);
    }

    [Test]
    public async Task A_page_on_another_site_is_refused()
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Stack.Mcp) { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await http.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // What a browser asks before a 2026-07-28 tools/call with a token (the CORS preflight).
    private const string McpRequestHeaders = "authorization, content-type, accept, mcp-protocol-version, mcp-method, mcp-name";

    [Test]
    public async Task An_allowed_page_may_send_the_MCP_headers()
    {
        using var response = await PreflightAsync(BrowserClientTests.AllowedOrigin);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo(BrowserClientTests.AllowedOrigin);
        var allowed = response.Headers.GetValues("Access-Control-Allow-Headers").SelectMany(h => h.Split(',')).Select(h => h.Trim().ToLowerInvariant());
        await Assert.That(allowed).IsEquivalentTo(McpRequestHeaders.Split(", "));
    }

    [Test]
    public async Task Another_page_may_not_even_ask()
    {
        using var response = await PreflightAsync("http://evil.example");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(response.Headers.Contains("Access-Control-Allow-Origin")).IsFalse();
    }

    private static async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, Stack.Mcp);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", McpRequestHeaders);
        return await http.SendAsync(request);
    }
}
