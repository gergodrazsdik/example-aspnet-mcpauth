using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpAuth.Server;

/// <summary>The MCP tools: what a client can call.</summary>
[McpServerToolType]
public sealed class NoteTools(NoteStore notes)
{
    /// <summary>Lists the notes.</summary>
    [McpServerTool(Name = "list_notes", Title = "List notes", ReadOnly = true)]
    [Description("Lists every note, oldest first, with its author and when it was written.")]
    public IReadOnlyList<Note> ListNotes() => notes.All();
}
