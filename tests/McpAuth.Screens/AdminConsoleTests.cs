using Microsoft.Playwright;
using TUnit.Playwright;

namespace McpAuth.Screens;

/// <summary>Screenshots of the demo realm in Keycloak's admin console (dev-only admin login).</summary>
public class AdminConsoleTests : PageTest
{
    private const string Console = Stack.Keycloak + "/admin/master/console/#/demo";

    public override BrowserNewContextOptions ContextOptions(TestContext testContext) => Stack.PageOptions;

    [Before(Test)]
    public async Task SignInAsAdmin()
    {
        await Stack.RequireRunningAsync();
        await Page.GotoAsync(Console);
        await Page.GetByLabel("Username or email").FillAsync("admin");
        await Page.GetByLabel("Password", new() { Exact = true }).FillAsync("admin");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Navigation)).ToBeVisibleAsync();
    }

    [Test]
    public async Task Users()
    {
        await Page.GotoAsync($"{Console}/users");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "alice" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "bob" })).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Stack.Shot("02-realm-users.png") });
    }

    [Test]
    public async Task Client_scopes()
    {
        await Page.GotoAsync($"{Console}/client-scopes");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "notes:write" })).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Stack.Shot("02-client-scopes.png") });
    }

    [Test]
    public async Task Scope_grants_only_its_role()
    {
        // notes:write maps to the notes-writer role: Keycloak grants the scope only to users who
        // hold it (fullScopeAllowed is off on every client).
        await Page.GotoAsync($"{Console}/client-scopes");
        await Page.GetByRole(AriaRole.Link, new() { Name = "notes:write" }).ClickAsync();
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Scope" }).ClickAsync();
        await Expect(Page.GetByText("notes-writer")).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Stack.Shot("02-scope-role-mapping.png") });
    }

    [Test]
    public async Task Console_client()
    {
        await Page.GotoAsync($"{Console}/clients");
        await Page.GetByRole(AriaRole.Link, new() { Name = "mcpauth-console" }).ClickAsync();
        var redirectUris = Page.GetByText("Valid redirect URIs").First;
        await redirectUris.ScrollIntoViewIfNeededAsync();
        await Expect(redirectUris).ToBeInViewportAsync();
        await Page.ScreenshotAsync(new() { Path = Stack.Shot("02-client.png") });
    }

    [Test]
    public async Task Registration_policies()
    {
        await Page.GotoAsync($"{Console}/clients");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Client registration" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Trusted Hosts" })).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Stack.Shot("07-registration-policies.png") });
    }

    [Test]
    public async Task Pkce_client_policy()
    {
        // The "Policies" subtab's accessible name is its aria-label ("Client policies subtab"), so
        // go to its address instead.
        await Page.GotoAsync($"{Console}/realm-settings/client-policies/policies");
        await Expect(Page.GetByText("public-clients-use-pkce")).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Stack.Shot("08-pkce-policy.png") });
    }
}
