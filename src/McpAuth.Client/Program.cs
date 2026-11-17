using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Web;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// A console MCP client that signs in through the browser: the SDK finds Keycloak from the
// server's 401, sends the user to its login page (authorization code with PKCE), and this
// program receives the code on a loopback address (http://127.0.0.1:<free port>/callback).
//
//   dotnet run --project src/McpAuth.Client [-- [--read-only] [--add "<text>"] [--no-browser] [endpoint]]
//
//   --read-only  ask Keycloak for notes:read only, not notes:write
//   --add        add a note (needs notes:write)
//   --no-browser print the sign-in address instead of opening a browser
var noBrowser = args.Contains("--no-browser");
var readOnly = args.Contains("--read-only");
var addIndex = Array.IndexOf(args, "--add");
var noteText = addIndex >= 0 && addIndex + 1 < args.Length ? args[addIndex + 1] : null;
var endpoint = new Uri(args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && i != addIndex + 1).FirstOrDefault() ?? "http://localhost:5100/mcp");

// Keycloak accepts any port on the loopback address for the registered
// http://127.0.0.1/callback (RFC 8252), so take a free one.
var redirectUri = new Uri($"http://127.0.0.1:{FreePort()}/callback");

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = endpoint,
    TransportMode = HttpTransportMode.StreamableHttp,
    OAuth = new ClientOAuthOptions
    {
        // Registered in Keycloak's realm (keycloak/realm-demo.json): a public client, no secret.
        ClientId = "mcpauth-console",
        RedirectUri = redirectUri,
        AuthorizationCallbackHandler = (context, cancellationToken) =>
            SignInInBrowserAsync(context.AuthorizationUri, context.RedirectUri, noBrowser, cancellationToken),

        // The SDK asks for the scopes the server lists (notes:read notes:write) plus
        // offline_access; the selector has the last word. (ClientOAuthOptions.Scopes is only a
        // fallback for servers that list none.)
        ScopeSelector = readOnly ? scopes => scopes?.Where(s => s != "notes:write") : null,
    },
});

await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
{
    ClientInfo = new Implementation { Name = "mcpauth-console", Version = "1.0.0" },

    // Connecting includes signing in, so give the user as long as the sign-in waits (the
    // default is 60 seconds).
    InitializationTimeout = TimeSpan.FromMinutes(5),
});

Console.WriteLine($"Connected to {client.ServerInfo?.Title ?? client.ServerInfo?.Name ?? "the server"} at {endpoint}");
Console.WriteLine($"Instructions: {client.ServerInstructions}");

Console.WriteLine("\nTools:");
foreach (var tool in await client.ListToolsAsync())
{
    Console.WriteLine($"  {tool.Name}: {tool.Description}");
}

await CallAsync(client, "whoami");
if (noteText is not null)
{
    await CallAsync(client, "add_note", new Dictionary<string, object?> { ["text"] = noteText });
}

await CallAsync(client, "list_notes");

// Calls a tool and prints its text; a refusal comes back as a result with IsError set, its
// message written for the model (and so readable for people too).
static async Task CallAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
{
    Console.WriteLine($"\n{tool}:");
    var result = await client.CallToolAsync(tool, arguments);
    foreach (var block in result.Content.OfType<TextContentBlock>())
    {
        Console.WriteLine(result.IsError == true ? $"  refused: {block.Text}" : block.Text);
    }
}

// Opens Keycloak's login page and waits for the browser to come back to the loopback address
// with the authorization code. The SDK made the URL (with PKCE, state and resource) and
// exchanges the code for tokens afterwards.
static async Task<AuthorizationResult?> SignInInBrowserAsync(Uri authorizationUri, Uri redirectUri, bool noBrowser, CancellationToken cancellationToken)
{
    using var listener = new HttpListener();
    listener.Prefixes.Add(redirectUri.GetLeftPart(UriPartial.Authority) + "/");
    listener.Start();

    Console.WriteLine($"Sign in to continue. Open this address if no browser opens:\n{authorizationUri}\n");
    if (!noBrowser)
    {
        OpenBrowser(authorizationUri);
    }

    // Wait for the redirect to the callback path; ignore anything else (such as /favicon.ico).
    HttpListenerContext context;
    do
    {
        context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        if (context.Request.Url?.AbsolutePath != redirectUri.AbsolutePath)
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
    }
    while (context.Request.Url?.AbsolutePath != redirectUri.AbsolutePath);

    var query = HttpUtility.ParseQueryString(context.Request.Url!.Query);
    var error = query["error"];
    var page = error is null
        ? "Signed in. You can close this tab and go back to the terminal."
        : $"Sign-in failed: {error}. Go back to the terminal.";
    var body = System.Text.Encoding.UTF8.GetBytes($"<!doctype html><title>MCP Auth console</title><p>{WebUtility.HtmlEncode(page)}</p>");
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.OutputStream.WriteAsync(body, cancellationToken);
    context.Response.Close();

    if (error is not null)
    {
        Console.Error.WriteLine($"Sign-in failed: {error} {query["error_description"]}");
        return null;
    }

    Console.WriteLine("Signed in.");
    return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
}

static void OpenBrowser(Uri uri)
{
    try
    {
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        // No browser to start (a server, a container): the address printed above still works.
    }
}

static int FreePort()
{
    using var socket = new TcpListener(IPAddress.Loopback, 0);
    socket.Start();
    return ((IPEndPoint)socket.LocalEndpoint).Port;
}
