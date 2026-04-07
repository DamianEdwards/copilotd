using System.CommandLine;
using Copilotd.Commands;
using Copilotd.Infrastructure;
using Copilotd.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Copilotd;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await ConsoleOutput.RunWithErrorHandling(async () =>
        {
            var consoleLogLevel = ParseLogLevel(args);
            var services = ConfigureServices(consoleLogLevel);

            var rootCommand = new RootCommand("copilotd - GitHub issue dispatch daemon for Copilot CLI");

            rootCommand.Subcommands.Add(InitCommand.Create(services));
            rootCommand.Subcommands.Add(ConfigCommand.Create(services));
            rootCommand.Subcommands.Add(RulesCommand.Create(services));
            rootCommand.Subcommands.Add(RunCommand.Create(services));
            rootCommand.Subcommands.Add(StartCommand.Create(services));
            rootCommand.Subcommands.Add(StopCommand.Create(services));
            rootCommand.Subcommands.Add(StatusCommand.Create(services));
            rootCommand.Subcommands.Add(SessionCommand.Create(services));
            rootCommand.Subcommands.Add(ShutdownInstanceCommand.Create());

            var parseResult = rootCommand.Parse(args, new ParserConfiguration());
            return await parseResult.InvokeAsync(new InvocationConfiguration());
        });
    }

    private static IServiceProvider ConfigureServices(LogLevel? consoleLogLevel)
    {
        var serviceCollection = new ServiceCollection();

        serviceCollection.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new FileLoggerProvider());

            if (consoleLogLevel is { } level)
            {
                builder.AddProvider(new StderrLoggerProvider(level));
            }
        });

        serviceCollection.AddSingleton<StateStore>();
        serviceCollection.AddSingleton<ProcessManager>();
        serviceCollection.AddSingleton<GhCliService>();
        serviceCollection.AddSingleton<CopilotCliService>();
        serviceCollection.AddSingleton<ReconciliationEngine>();

        return serviceCollection.BuildServiceProvider();
    }

    private static LogLevel? ParseLogLevel(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            // Handle --log-level value
            if (string.Equals(args[i], "--log-level", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return ParseLogLevelValue(args[i + 1]);
            }

            // Handle --log-level=value
            if (args[i].StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
            {
                return ParseLogLevelValue(args[i]["--log-level=".Length..]);
            }
        }

        // Default to Information level for the 'run' and 'start' commands so users can see
        // poll cycle activity and session status changes without needing --log-level
        var command = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (string.Equals(command, "run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "start", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Information;
        }

        return null;
    }

    private static LogLevel ParseLogLevelValue(string value) => value.ToLowerInvariant() switch
    {
        "debug" or "trace" => LogLevel.Debug,
        "info" or "information" => LogLevel.Information,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information,
    };
}
