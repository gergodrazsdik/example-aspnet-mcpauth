using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace McpAuth.Screens;

/// <summary>The running example: Keycloak, the MCP server, the console client, and where screenshots go.</summary>
public static class Stack
{
    public const string Keycloak = "http://localhost:8180";
    public const string Realm = Keycloak + "/realms/demo";
    public const string Mcp = "http://localhost:5100/mcp";

    /// <summary>The repository's root: the folder with global.json, above the test's build output.</summary>
    public static string Root { get; } = FindRoot(AppContext.BaseDirectory);

    /// <summary>notes/screenshots, where the article's images go.</summary>
    public static string Screenshots { get; } = Directory.CreateDirectory(Path.Combine(Root, "notes", "screenshots")).FullName;

    /// <summary>Every test page: 1280×800, light theme, English.</summary>
    public static BrowserNewContextOptions PageOptions => new()
    {
        ViewportSize = new() { Width = 1280, Height = 800 },
        ColorScheme = ColorScheme.Light,
        Locale = "en-US",
    };

    public static string Shot(string name) => Path.Combine(Screenshots, name);

    /// <summary>Fails with a clear message when Keycloak or the MCP server isn't running.</summary>
    public static async Task RequireRunningAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Require(http, $"{Realm}/.well-known/openid-configuration", "Keycloak (podman compose up -d)");
        await Require(http, "http://localhost:5100/.well-known/oauth-protected-resource/mcp", "the MCP server (dotnet run --project src/McpAuth.Server)");

        static async Task Require(HttpClient http, string url, string what)
        {
            try
            {
                using var response = await http.GetAsync(url);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidOperationException($"Start {what} first: {url} didn't answer ({ex.Message}).", ex);
            }
        }
    }

    /// <summary>
    /// Forgets what a user allowed the console client, so the next sign-in shows the consent page.
    /// Uses the dev-only admin login from compose.yaml.
    /// </summary>
    public static async Task RevokeConsentAsync(string username, string clientId = "mcpauth-console")
    {
        using var http = new HttpClient();
        using var login = await http.PostAsync($"{Keycloak}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = "admin",
            ["password"] = "admin",
        }));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var users = await http.GetFromJsonAsync<JsonElement>($"{Keycloak}/admin/realms/demo/users?exact=true&username={username}");
        var id = users[0].GetProperty("id").GetString();
        using var revoke = await http.DeleteAsync($"{Keycloak}/admin/realms/demo/users/{id}/consents/{clientId}");
        // 404: there was nothing to forget.
        if (!revoke.IsSuccessStatusCode && revoke.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            revoke.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Runs the console client with --no-browser and the given arguments, signs in as
    /// <paramref name="user"/> on <paramref name="page"/> when it prints the sign-in address, and
    /// returns everything it printed. Screenshot names are optional.
    /// </summary>
    public static async Task<string> RunClientAsync(IPage page, string user, string[] arguments, string? loginShot = null, string? consentShot = null, string? signedInShot = null)
    {
        var client = Path.Combine(AppContext.BaseDirectory, "..", "..", "McpAuth.Client", new DirectoryInfo(AppContext.BaseDirectory).Name, "McpAuth.Client.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in (string[])[client, "--no-browser", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start the console client.");
        var output = new StringBuilder();
        var signInUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(e.Data);
            }

            if (e.Data.StartsWith(Realm + "/", StringComparison.Ordinal))
            {
                signInUrl.TrySetResult(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            lock (output)
            {
                output.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var url = await signInUrl.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await SignInAsync(page, url, user, loginShot, consentShot);

            // Back on the client's loopback page.
            var signedIn = page.GetByText("Signed in. You can close this tab");
            await Assertions.Expect(signedIn).ToBeVisibleAsync();
            if (signedInShot is not null)
            {
                await page.ScreenshotAsync(new() { Path = Shot(signedInShot), Clip = new() { X = 0, Y = 0, Width = 1280, Height = 120 } });
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        lock (output)
        {
            return output.ToString();
        }
    }

    /// <summary>Signs in on Keycloak's login page and allows access on the consent page, if it comes.</summary>
    public static async Task SignInAsync(IPage page, string url, string user, string? loginShot = null, string? consentShot = null)
    {
        await page.GotoAsync(url);
        var username = page.GetByLabel("Username or email");
        await Assertions.Expect(username).ToBeVisibleAsync();
        if (loginShot is not null)
        {
            await page.ScreenshotAsync(new() { Path = Shot(loginShot) });
        }

        // The demo users' passwords are their names (dev-only; see README.md).
        await username.FillAsync(user);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(user);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();

        // Keycloak asks for consent once per user and client; after that it goes straight back.
        var allow = page.GetByRole(AriaRole.Button, new() { Name = "Yes" });
        var done = page.GetByText("Signed in.");
        await Assertions.Expect(allow.Or(done)).ToBeVisibleAsync();
        if (await allow.IsVisibleAsync())
        {
            if (consentShot is not null)
            {
                await page.ScreenshotAsync(new() { Path = Shot(consentShot) });
            }

            await allow.ClickAsync();
        }
    }

    private static string FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No global.json above {start}.");
    }
}
