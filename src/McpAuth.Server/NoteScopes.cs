using System.Security.Claims;

namespace McpAuth.Server;

/// <summary>The OAuth scopes of the notes, as Keycloak's realm defines them.</summary>
public static class NoteScopes
{
    /// <summary>Read the notes.</summary>
    public const string Read = "notes:read";

    /// <summary>Add notes.</summary>
    public const string Write = "notes:write";

    /// <summary>Gets the scopes of the user's access token (the "scope" claim, space-separated).</summary>
    public static IReadOnlyList<string> Of(ClaimsPrincipal user) =>
        user.FindFirstValue("scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>
    /// What the token allows, in a sentence for the model: the server's instructions and whoami
    /// say it, so a client knows before it tries a tool it may not use.
    /// </summary>
    public static string Rights(ClaimsPrincipal user)
    {
        var scopes = Of(user);
        return (scopes.Contains(Read), scopes.Contains(Write)) switch
        {
            (true, true) => "This token lets you read notes (list_notes) and add notes in their name (add_note).",
            (true, false) => "This token lets you read notes (list_notes) but not add them: add_note will be refused, so tell the user instead of trying.",
            (false, true) => "This token lets you add notes in their name (add_note) but not read them.",
            _ => "This token allows no note tools; only whoami works.",
        };
    }
}
