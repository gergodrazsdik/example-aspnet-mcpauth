using System.Security.Claims;
using McpAuth.Server;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// The authorization server (Keycloak's realm) and this MCP endpoint's own URL, the resource
// that tokens must be issued for.
var authority = builder.Configuration["Auth:Authority"]
    ?? throw new InvalidOperationException("Set Auth:Authority to the Keycloak realm's URL.");
var resource = builder.Configuration["Auth:Resource"]
    ?? throw new InvalidOperationException("Set Auth:Resource to the MCP endpoint's URL.");

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

app.UseAuthentication();
app.UseAuthorization();

// Only signed-in users: without a valid token, the endpoint answers 401.
app.MapMcp("/mcp").RequireAuthorization();

app.Run();
