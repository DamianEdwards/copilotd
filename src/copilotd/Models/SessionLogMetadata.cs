namespace Copilotd.Models;

public sealed class SessionLogMetadata
{
    public string SessionId { get; set; } = "";

    public string SessionType { get; set; } = "";

    public string? IssueKey { get; set; }

    public string Status { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
