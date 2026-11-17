using McpAuth.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<NoteStore>();

// The MCP server: Streamable HTTP, stateless (no session to keep, so it scales out without
// sticky sessions), with the note tools.
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<NoteTools>();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
