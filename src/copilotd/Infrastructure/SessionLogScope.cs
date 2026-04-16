using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

public static class SessionLogScope
{
    public const string SessionIdKey = "CopilotdSessionId";

    public static IDisposable Begin(ILogger logger, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return EmptyScope.Instance;

        return logger.BeginScope(new Dictionary<string, object?>
        {
            [SessionIdKey] = sessionId
        }) ?? EmptyScope.Instance;
    }

    public static string? GetCurrentSessionId(IExternalScopeProvider? scopeProvider)
    {
        if (scopeProvider is null)
            return null;

        string? sessionId = null;
        scopeProvider.ForEachScope((scope, _) =>
        {
            if (sessionId is not null)
                return;

            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (string.Equals(key, SessionIdKey, StringComparison.Ordinal)
                        && value is string candidate
                        && !string.IsNullOrWhiteSpace(candidate))
                    {
                        sessionId = candidate;
                        return;
                    }
                }
            }
        }, state: 0);

        return sessionId;
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose() { }
    }
}
