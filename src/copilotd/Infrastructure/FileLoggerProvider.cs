using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Rolling file logger that writes under copilotd's home directory.
/// Non-daemon invocations log directly under ~/.copilotd/logs and daemon run
/// invocations log under ~/.copilotd/logs/daemon_&lt;uuid&gt;.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly LogFileManager _logFileManager;
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(LogFileManager logFileManager, LogLevel minLevel = LogLevel.Debug)
    {
        _logFileManager = logFileManager;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _logFileManager, _minLevel);

    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly LogFileManager _logFileManager;
    private readonly LogLevel _minLevel;

    public FileLogger(string category, LogFileManager logFileManager, LogLevel minLevel)
    {
        _category = category;
        _logFileManager = logFileManager;
        _minLevel = minLevel;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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
            var filePath = _logFileManager.GetCurrentProcessLogFilePath();
            File.AppendAllText(filePath, line);
        }
        catch
        {
            // Swallow logging failures
        }
    }
}
