using System.CommandLine;
using System.CommandLine.Parsing;
using Copilotd.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class LogsCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("logs", "Show or clear copilotd log files");
        command.Subcommands.Add(CreateClearCommand(services));

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var logFileManager = services.GetRequiredService<LogFileManager>();
                ConsoleOutput.Info($"Logs: {logFileManager.GetLogsRootDirectoryForDisplay()}");
                return 0;
            }, logger);
        });

        return command;
    }

    private static Command CreateClearCommand(IServiceProvider services)
    {
        var command = new Command("clear", "Clear copilotd log files");

        var daysOption = new Option<int?>("--days")
        {
            Description = "Delete log files older than this many days"
        };
        command.Options.Add(daysOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var logFileManager = services.GetRequiredService<LogFileManager>();

                var days = parseResult.GetValue(daysOption);
                if (days is <= 0)
                {
                    ConsoleOutput.Error("--days must be a positive integer.");
                    return 1;
                }

                if (days is null && !AnsiConsole.Confirm("Clear all log files except active log files?", false))
                {
                    ConsoleOutput.Warning("Log clear cancelled.");
                    return 0;
                }

                string? activeDaemonInstanceId = null;
                if (stateStore.IsLockHeld() && stateStore.ReadDaemonPid() is { LogInstanceId: { Length: > 0 } logInstanceId })
                    activeDaemonInstanceId = logInstanceId;

                var clearResult = logFileManager.ClearLogs(days, activeDaemonInstanceId);
                foreach (var warning in clearResult.Warnings)
                    ConsoleOutput.Warning(warning);

                ConsoleOutput.Success(days is { } age
                    ? $"Cleared {clearResult.DeletedCount} log file(s) older than {age} day(s)."
                    : $"Cleared {clearResult.DeletedCount} log file(s).");
                ConsoleOutput.Info($"Logs: {logFileManager.GetLogsRootDirectoryForDisplay()}");
                return 0;
            }, logger);
        });

        return command;
    }
}
