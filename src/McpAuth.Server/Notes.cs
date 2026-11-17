namespace McpAuth.Server;

/// <summary>A note: who wrote it, when, and its text.</summary>
public sealed record Note(int Id, string Author, DateTimeOffset Created, string Text);

/// <summary>The notes, in memory: the example has no database.</summary>
public sealed class NoteStore
{
    private readonly Lock _lock = new();
    private readonly List<Note> _notes =
    [
        new(1, "alice", DateTimeOffset.Parse("2026-11-16T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "Buy coffee beans."),
        new(2, "bob", DateTimeOffset.Parse("2026-11-16T10:30:00Z", System.Globalization.CultureInfo.InvariantCulture), "Read RFC 9728 on the train."),
    ];

    /// <summary>Gets every note, oldest first.</summary>
    public IReadOnlyList<Note> All()
    {
        lock (_lock)
        {
            return [.. _notes];
        }
    }

    /// <summary>Adds a note and returns it.</summary>
    public Note Add(string author, string text, TimeProvider time)
    {
        lock (_lock)
        {
            var note = new Note(_notes.Count == 0 ? 1 : _notes[^1].Id + 1, author, time.GetUtcNow(), text);
            _notes.Add(note);
            return note;
        }
    }
}
