using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace McpAuth.Screens;

/// <summary>
/// A browser-based MCP client: a page on an allowed origin calls the endpoint with fetch, so the
/// browser enforces CORS for real (preflight, allowed headers, exposed headers).
/// </summary>
/// <remarks>
/// The page comes from a small server on port 6274, not from Playwright's routing: with routing
/// on, Playwright answers CORS preflights itself, and the test would pass whatever the server
/// allows. One test at a time, as they share the port (stop the MCP Inspector first).
/// </remarks>
[NotInParallel(nameof(BrowserClientTests))]
public class BrowserClientTests : PageTest
{
    /// <summary>The origin Mcp:AllowedOrigins lets in during development (the MCP Inspector's).</summary>
    public const string AllowedOrigin = "http://localhost:6274";

    // Same machine and port as the allowed origin, but another origin.
    private const string OtherOrigin = "http://127.0.0.1:6274";

    // Sends an MCP 2026-07-28 request from the page and returns the status and the challenge, or
    // the error when the browser refuses (a CORS failure is a TypeError with no response).
    private const string CallMcp = """
        async ([endpoint, method, name, token]) => {
          const headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": "2026-07-28",
            "Mcp-Method": method,
          };
          if (name) headers["Mcp-Name"] = name;
          if (token) headers["Authorization"] = `Bearer ${token}`;
          const params = name ? { name, arguments: {} } : {};
          try {
            const response = await fetch(endpoint, {
              method: "POST",
              headers,
              body: JSON.stringify({ jsonrpc: "2.0", id: 1, method, params }),
            });
            return { status: response.status, challenge: response.headers.get("WWW-Authenticate") };
          } catch (error) {
            return { status: 0, error: String(error) };
          }
        }
        """;

    public override BrowserNewContextOptions ContextOptions(TestContext testContext) => Stack.PageOptions;

    [Before(Test)]
    public async Task RequireStack() => await Stack.RequireRunningAsync();

    [Test]
    public async Task An_allowed_page_reads_the_challenge()
    {
        using var site = await OpenPageAsync(AllowedOrigin);

        var answer = await Page.EvaluateAsync<JsonElement>(CallMcp, new object?[] { Stack.Mcp, "tools/list", null, null });

        await Assert.That(answer.GetProperty("status").GetInt32()).IsEqualTo(401);
        await Assert.That(answer.GetProperty("challenge").GetString())
            .IsEqualTo("Bearer resource_metadata=\"http://localhost:5100/.well-known/oauth-protected-resource/mcp\", scope=\"notes:read notes:write\"");
    }

    [Test]
    public async Task An_allowed_page_calls_a_tool_with_a_rejected_token()
    {
        using var site = await OpenPageAsync(AllowedOrigin);

        // Every MCP header, Authorization and Mcp-Name included, gets through the preflight.
        var answer = await Page.EvaluateAsync<JsonElement>(CallMcp, new object?[] { Stack.Mcp, "tools/call", "list_notes", "not.a.token" });

        await Assert.That(answer.GetProperty("status").GetInt32()).IsEqualTo(401);
        await Assert.That(answer.GetProperty("challenge").GetString()).EndsWith(", error=\"invalid_token\"");
    }

    [Test]
    public async Task A_page_from_another_origin_gets_nothing()
    {
        using var site = await OpenPageAsync(OtherOrigin);

        var answer = await Page.EvaluateAsync<JsonElement>(CallMcp, new object?[] { Stack.Mcp, "tools/list", null, null });

        await Assert.That(answer.GetProperty("status").GetInt32()).IsEqualTo(0);
        await Assert.That(answer.GetProperty("error").GetString()).Contains("Failed to fetch");
    }

    // Serves an empty page at the origin until disposed, and opens it.
    private async Task<IDisposable> OpenPageAsync(string origin)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException($"Port 6274 is taken ({ex.Message}); stop the MCP Inspector and run the tests again.", ex);
        }

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    var body = "<!doctype html><title>Browser MCP client</title>"u8.ToArray();
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        });

        await Page.GotoAsync(origin + "/");
        return listener;
    }
}
