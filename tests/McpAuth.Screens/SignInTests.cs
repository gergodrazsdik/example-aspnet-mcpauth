using System.Text.Json;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace McpAuth.Screens;

/// <summary>
/// The console client signs in through Keycloak in a fresh browser (no session, so a real login),
/// and the server's tools answer according to the token. One at a time: the tests share Keycloak's
/// users and their consents.
/// </summary>
[NotInParallel(nameof(SignInTests))]
public class SignInTests : PageTest
{
    public override BrowserNewContextOptions ContextOptions(TestContext testContext) => Stack.PageOptions;

    [Before(Test)]
    public async Task RequireStack() => await Stack.RequireRunningAsync();

    [Test]
    public async Task Alice_signs_in_consents_and_adds_a_note()
    {
        await Stack.RevokeConsentAsync("alice");
        var text = $"Written by the end-to-end test at {DateTimeOffset.UtcNow:HH:mm:ss}.";

        var output = await Stack.RunClientAsync(Page, "alice", ["--add", text],
            loginShot: "02-login.png", consentShot: "05-consent.png", signedInShot: "05-signed-in.png");

        await Assert.That(output).Contains("Connected to MCP Auth example: notes");
        await Assert.That(output).Contains("You work for alice (Alice Writer)");
        await Assert.That(output).Contains("\"client\":\"mcpauth-console\"");
        await Assert.That(Scopes(output)).Contains("notes:write");
        await Assert.That(output).Contains("\"audience\":\"http://localhost:5100/mcp\"");
        await Assert.That(output).Contains($"\"author\":\"alice\"");
        await Assert.That(output).Contains(text);
        await Assert.That(output).DoesNotContain("refused:");
    }

    [Test]
    public async Task Bob_reads_notes_but_may_not_add_one()
    {
        var output = await Stack.RunClientAsync(Page, "bob", ["--add", "Bob tries to write."]);

        // Bob asked for notes:write too, and Keycloak left it out: he doesn't have the role.
        var scopes = Scopes(output);
        await Assert.That(scopes).Contains("notes:read");
        await Assert.That(scopes).DoesNotContain("notes:write");
        await Assert.That(output).Contains("refused: An error occurred invoking 'add_note': bob may not add notes with this token");
        await Assert.That(output).Contains("Read RFC 9728 on the train.");
        await Assert.That(output).DoesNotContain("Bob tries to write.");
    }

    [Test]
    public async Task Alice_signed_in_read_only_may_not_add_a_note()
    {
        // The consent page shows only what this sign-in asks for.
        await Stack.RevokeConsentAsync("alice");
        var output = await Stack.RunClientAsync(Page, "alice", ["--read-only", "--add", "Read-only alice tries to write."],
            consentShot: "06-consent-read-only.png");

        await Assert.That(Scopes(output)).DoesNotContain("notes:write");
        await Assert.That(output).Contains("This token lets you read notes (list_notes) but not add them");
        await Assert.That(output).Contains("refused: An error occurred invoking 'add_note': alice may not add notes with this token");
        await Assert.That(output).DoesNotContain("Read-only alice tries to write.");
    }

    [Test]
    public async Task Bob_signs_in_after_alice_in_the_same_browser()
    {
        // The console client asks for offline_access, and Keycloak (26.8) then keeps no browser
        // session after the sign-in, so the next run shows the login page again: the README's
        // quick start (alice, then bob) works without signing out.
        await Stack.RunClientAsync(Page, "alice", []);
        var output = await Stack.RunClientAsync(Page, "bob", []);

        await Assert.That(output).Contains("You work for bob");
    }

    // The scopes in whoami's answer (the line after "whoami:"), in whatever order Keycloak lists them.
    private static List<string> Scopes(string output)
    {
        var lines = output.Split('\n');
        var whoami = lines[Array.FindIndex(lines, line => line.Trim() == "whoami:") + 1];
        using var json = JsonDocument.Parse(whoami);
        return [.. json.RootElement.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString() ?? "")];
    }
}
