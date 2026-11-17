namespace McpAuth.Server;

/// <summary>The OAuth scopes of the notes, as Keycloak's realm defines them.</summary>
public static class NoteScopes
{
    /// <summary>Read the notes.</summary>
    public const string Read = "notes:read";

    /// <summary>Add notes.</summary>
    public const string Write = "notes:write";
}
