using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Rolling file logger that writes under copilotd's home directory.
/// Daemon logs go to ~/.copilotd/logs/daemon and session-scoped logs go to
/// ~/.copilotd/logs/&lt;session-id&gt;, with daily rollover and 10 MB file rollover.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly SessionLogManager _sessionLogManager;
    private readonly LogLevel _minLevel;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public FileLoggerProvider(SessionLogManager sessionLogManager, LogLevel minLevel = LogLevel.Debug)
    {
        _sessionLogManager = sessionLogManager;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _sessionLogManager, () => _scopeProvider, _minLevel);

    public void Dispose() { }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopeProvider = scopeProvider;
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly SessionLogManager _sessionLogManager;
    private readonly Func<IExternalScopeProvider> _scopeProviderAccessor;
    private readonly LogLevel _minLevel;
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    public FileLogger(string category, SessionLogManager sessionLogManager, Func<IExternalScopeProvider> scopeProviderAccessor, LogLevel minLevel)
    {
        _category = category;
        _sessionLogManager = sessionLogManager;
        _scopeProviderAccessor = scopeProviderAccessor;
        _minLevel = minLevel;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _scopeProviderAccessor().Push(state);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = logLevel.ToString().ToUpperInvariant()[..4];
        var line = $"[{timestamp}] [{level}] [{_category}] {message}";

        if (exception is not null)
            line += Environment.NewLine + exception;

        line += Environment.NewLine;

        try
        {
            var filePath = GetCurrentLogFile();
            File.AppendAllText(filePath, line);
        }
        catch
        {
            // Swallow logging failures
        }
    }

    private string GetCurrentLogFile()
    {
        var sessionId = SessionLogScope.GetCurrentSessionId(_scopeProviderAccessor());
        var logDirectory = string.IsNullOrWhiteSpace(sessionId)
            ? _sessionLogManager.GetDaemonLogDirectory()
            : _sessionLogManager.GetSessionLogDirectory(sessionId);
        Directory.CreateDirectory(logDirectory);
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var basePath = Path.Combine(logDirectory, $"copilotd-{date}.log");

        // Check for rollover
        if (File.Exists(basePath))
        {
            var info = new FileInfo(basePath);
            if (info.Length >= MaxFileSize)
            {
                // Find next available rollover name
                for (var i = 1; ; i++)
                {
                    var rolledPath = Path.Combine(logDirectory, $"copilotd-{date}-{i}.log");
                    if (!File.Exists(rolledPath) || new FileInfo(rolledPath).Length < MaxFileSize)
                        return rolledPath;
                }
            }
        }

        return basePath;
    }
}
