using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using McpAuth.Server;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Timestamps, to line the server's log up with Keycloak's events.
builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss.fff ");

// The authorization server (Keycloak's realm) and this MCP endpoint's own URL, the resource
// that tokens must be issued for.
var authority = builder.Configuration["Auth:Authority"]
    ?? throw new InvalidOperationException("Set Auth:Authority to the Keycloak realm's URL.");
var resource = builder.Configuration["Auth:Resource"]
    ?? throw new InvalidOperationException("Set Auth:Resource to the MCP endpoint's URL.");

// Plain HTTP is for the local demo only: tokens travel in the Authorization header.
if (!builder.Environment.IsDevelopment()
    && (!authority.StartsWith("https://", StringComparison.Ordinal) || !resource.StartsWith("https://", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("Auth:Authority and Auth:Resource must be https URLs outside Development.");
}

// Web pages allowed to call the endpoint from a browser (a browser-based MCP client such as the
// MCP Inspector). Other clients send no Origin header and aren't affected.
var allowedOrigins = builder.Configuration.GetSection("Mcp:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddAuthentication(options =>
    {
        // JwtBearer validates the token. The MCP SDK's scheme sends the challenge: a 401 whose
        // WWW-Authenticate header points the client at the protected resource metadata.
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        // Keycloak's signing keys and issuer come from the realm's OIDC discovery document.
        options.Authority = authority;

        // The token must be for this endpoint: Keycloak's audience mapper puts its URL in "aud".
        options.Audience = resource;

        // Keycloak runs on plain HTTP in development only.
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        // Keep the claims as Keycloak names them (sub, scope, roles, preferred_username).
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "preferred_username";
        options.TokenValidationParameters.RoleClaimType = "roles";

        // Keycloak's access tokens live 5 minutes; the default 5-minute clock skew would double
        // that. Allow only for small clock differences.
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    })
    .AddMcp(options =>
    {
        // Served at /.well-known/oauth-protected-resource/mcp (and without /mcp): it names the
        // resource and the authorization server that issues tokens for it (RFC 9728), and the
        // scopes a client should ask for.
        options.ResourceMetadata = new()
        {
            Resource = resource,
            AuthorizationServers = { authority },
            ScopesSupported = [NoteScopes.Read, NoteScopes.Write],
            ResourceName = "MCP Auth example: notes",
        };
    });

builder.Services.AddAuthorization();

// Per client address, before authentication: a flood of requests (with or without a token) is
// turned away before the server spends any work on validating tokens. Behind a reverse proxy,
// add the forwarded headers middleware first, or every request shares the proxy's address.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimit:PermitLimit", 120),
                Window = TimeSpan.FromMinutes(1),
            }));
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
});

// Browser clients: the allowed pages may send the headers MCP requests carry and read
// WWW-Authenticate, which points to the protected resource metadata.
// - Mcp-Method and Mcp-Name: MCP 2026-07-28 requires them (Mcp-Name on tools/call, resources/read
//   and prompts/get). CORS has no prefix pattern for Mcp-Param-* headers: each would be listed by
//   name (or every header allowed). No tool here asks for one (x-mcp-header), so none are listed.
// - No Mcp-Session-Id, Last-Event-ID or DELETE: the server is stateless, so it never issues a
//   session ID, and clients on older revisions (which still start with initialize) never send one
//   or end a session. 2026-07-28 removed sessions altogether.
// - GET stays: clients on older revisions may open the optional event stream with GET, and the
//   stateless endpoint answers 405, which they expect. Without GET here, they'd see a CORS error.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .WithMethods("POST", "GET")
    .WithHeaders("Authorization", "Content-Type", "Accept", "Mcp-Protocol-Version", "Mcp-Method", "Mcp-Name")
    .WithExposedHeaders("WWW-Authenticate")));

builder.Services.AddSingleton<NoteStore>();
builder.Services.AddSingleton(TimeProvider.System);

// The tools read the signed-in user from the request.
builder.Services.AddHttpContextAccessor();

// The MCP server: Streamable HTTP, stateless (no session to keep, so it scales out without
// sticky sessions), with the note tools.
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.SessionMode = HttpServerSessionMode.Stateless;

        // Stateless: this runs for every request, after authentication. The instructions tell
        // the client who it works for and what the token allows, so it needn't learn that from
        // refusals.
        options.ConfigureSessionOptions = (context, server, _) =>
        {
            var user = context.User;
            server.ServerInfo = new() { Name = "mcpauth-notes", Title = "MCP Auth example: notes", Version = "1.0.0" };
            server.ServerInstructions =
                $"You work for {user.Identity?.Name} ({user.FindFirstValue("name")}), who signed in through Keycloak. "
                + NoteScopes.Rights(user)
                + " whoami shows the token's scopes and roles. Notes are short texts; add_note writes one in the user's name.";
            return Task.CompletedTask;
        };
    })
    .WithTools<NoteTools>();

var app = builder.Build();

app.UseRateLimiter();

// The MCP spec requires servers to check the Origin header against DNS rebinding: a page on another
// site must not reach this endpoint through the user's browser. Requests without one (desktop
// and command-line clients) pass; AllowedHosts (appsettings.json) checks the Host header too.
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
    {
        app.Logger.LogWarning("Refused a request from origin {Origin}; allowed: Mcp:AllowedOrigins.", origin);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next(context);
});

app.UseCors();

// The SDK's challenge only says where the metadata is. Two parameters complete it:
// - scope: the scopes to ask for. The MCP spec says servers SHOULD send them; without them,
//   clients fall back to the metadata's scopes_supported.
// - error="invalid_token" when a token came and was rejected (expired, wrong audience, Keycloak's
//   keys changed), so a client can tell that from "you sent no token" (RFC 6750). It should renew
//   the token, not ask the user to sign in, and some clients (VS Code) otherwise fail the call
//   before renewing.
var challengeScope = $", scope=\"{NoteScopes.Read} {NoteScopes.Write}\"";
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var challenge = context.Response.Headers.WWWAuthenticate.ToString();
        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized
            && challenge.StartsWith("Bearer ", StringComparison.Ordinal)
            && !challenge.Contains("scope=", StringComparison.Ordinal))
        {
            context.Response.Headers.WWWAuthenticate = challenge + challengeScope
                + (context.Request.Headers.Authorization.Count > 0 ? ", error=\"invalid_token\"" : "");
        }

        return Task.CompletedTask;
    });

    await next(context);
});

app.UseAuthentication();
app.UseAuthorization();

// Only signed-in users: without a valid token, the endpoint answers 401.
app.MapMcp("/mcp").RequireAuthorization();

app.Run();
