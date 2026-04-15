using Copilotd.Models;

namespace Copilotd.Infrastructure;

/// <summary>
/// Builds github.com task URLs for Copilot remote sessions.
/// </summary>
public static class GitHubRemoteSessionUrl
{
    public const string ControlSessionRepo = "DamianEdwards/copilotd";

    public static string? Build(DispatchSession session, string? currentUser)
        => Build(session.Repo, session.CopilotSessionId, currentUser);

    public static string? BuildControl(ControlSessionInfo session, string? currentUser)
        => Build(ControlSessionRepo, session.CopilotSessionId, currentUser);

    public static string? Build(string? repo, string? sessionId, string? currentUser)
    {
        if (string.IsNullOrWhiteSpace(repo)
            || string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(currentUser))
        {
            return null;
        }

        return $"https://github.com/{repo}/tasks/{Uri.EscapeDataString(sessionId)}?author={Uri.EscapeDataString(currentUser)}";
    }
}
