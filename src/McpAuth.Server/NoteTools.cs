using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpAuth.Server;

/// <summary>Who signed in and what their token allows; never the token itself.</summary>
public sealed record Identity(
    string User,
    string? Name,
    string Subject,
    string? Client,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Roles,
    string? Audience,
    DateTimeOffset? Expires,
    string Allows);

/// <summary>
/// The MCP tools. Each one checks the access token's scopes itself: tool annotations such as
/// ReadOnly are hints for the client, not a guard, so the server enforces what a token may do.
/// </summary>
[McpServerToolType]
public sealed class NoteTools(NoteStore notes, IHttpContextAccessor http, TimeProvider time)
{
    private ClaimsPrincipal User => http.HttpContext?.User
        ?? throw new InvalidOperationException("No HTTP request: the tools run only over HTTP.");

    /// <summary>Says who signed in and what the token allows.</summary>
    [McpServerTool(Name = "whoami", Title = "Who am I", ReadOnly = true)]
    [Description("Says who signed in, through which app, and what this access token allows (its scopes and roles). Never returns the token.")]
    public Identity WhoAmI()
    {
        var user = User;
        var expires = long.TryParse(user.FindFirstValue("exp"), out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp) : (DateTimeOffset?)null;
        return new Identity(
            User: user.Identity?.Name ?? "(unknown)",
            Name: user.FindFirstValue("name"),
            Subject: user.FindFirstValue("sub") ?? "(none)",
            Client: user.FindFirstValue("azp"),
            Scopes: NoteScopes.Of(user),
            Roles: [.. user.FindAll("roles").Select(c => c.Value)],
            Audience: user.FindFirstValue("aud"),
            Expires: expires,
            Allows: NoteScopes.Rights(user));
    }

    /// <summary>Lists the notes.</summary>
    [McpServerTool(Name = "list_notes", Title = "List notes", ReadOnly = true)]
    [Description("Lists every note, oldest first, with its author and when it was written. Needs the notes:read scope.")]
    public IReadOnlyList<Note> ListNotes()
    {
        Require(NoteScopes.Read, "read notes");
        return notes.All();
    }

    /// <summary>Adds a note in the signed-in user's name.</summary>
    [McpServerTool(Name = "add_note", Title = "Add a note", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Adds a note in the signed-in user's name and returns it. Needs the notes:write scope.")]
    public Note AddNote([Description("The note's text: 1 to 500 characters.")] string text)
    {
        Require(NoteScopes.Write, "add notes");
        text = text?.Trim() ?? "";
        if (text.Length is 0 or > 500)
        {
            throw new McpException($"text must be 1 to 500 characters; this one has {text.Length}.");
        }

        return notes.Add(User.Identity?.Name ?? "(unknown)", text, time);
    }

    // A refusal the model can act on: what's missing, why, and what to do instead.
    private void Require(string scope, string action)
    {
        var user = User;
        var scopes = NoteScopes.Of(user);
        if (!scopes.Contains(scope))
        {
            throw new McpException(
                $"{user.Identity?.Name} may not {action} with this token: it needs the {scope} scope and has "
                + $"{(scopes.Count == 0 ? "none" : string.Join(", ", scopes))}. Keycloak grants {scope} only to users "
                + $"with the matching role who allow it when signing in. Don't retry; tell the user.");
        }
    }
}
