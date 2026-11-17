using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// A console MCP client: connects to the server, lists its tools and calls list_notes.
var endpoint = new Uri(args.FirstOrDefault() ?? "http://localhost:5100/mcp");

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = endpoint,
    TransportMode = HttpTransportMode.StreamableHttp,
});

await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
{
    ClientInfo = new Implementation { Name = "mcpauth-console", Version = "1.0.0" },
});

Console.WriteLine($"Connected to {client.ServerInfo?.Name ?? "the server"} at {endpoint}");

Console.WriteLine("\nTools:");
foreach (var tool in await client.ListToolsAsync())
{
    Console.WriteLine($"  {tool.Name}: {tool.Description}");
}

Console.WriteLine("\nlist_notes:");
var result = await client.CallToolAsync("list_notes");
foreach (var block in result.Content.OfType<TextContentBlock>())
{
    Console.WriteLine(block.Text);
}
