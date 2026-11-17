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
        // resource and the authorization server that issues tokens for it (RFC 9728).
        options.ResourceMetadata = new()
        {
            Resource = resource,
            AuthorizationServers = { authority },
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSingleton<NoteStore>();

// The MCP server: Streamable HTTP, stateless (no session to keep, so it scales out without
// sticky sessions), with the note tools.
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<NoteTools>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Only signed-in users: without a valid token, the endpoint answers 401.
app.MapMcp("/mcp").RequireAuthorization();

app.Run();
