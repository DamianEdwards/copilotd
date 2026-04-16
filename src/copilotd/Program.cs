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
            var runtimeContext = services.GetRequiredService<RuntimeContext>();
            var stateStore = services.GetRequiredService<StateStore>();
            var updateService = services.GetRequiredService<UpdateService>();

            if (IsStartupRepairCommand(args) && stateStore.IsUpdateLockHeld())
            {
                ConsoleOutput.Error("A self-update is already in progress. Wait for it to finish before starting copilotd.");
                return 1;
            }

            if (ShouldRunStartupRepair(args, runtimeContext, stateStore)
                && await updateService.RepairInterruptedInstallAsync(skipProvenance: false, CancellationToken.None) is { } repairResult)
            {
                if (!repairResult.Succeeded)
                {
                    ConsoleOutput.Error(repairResult.Message);
                    return 1;
                }

                ConsoleOutput.Info(repairResult.Message);

                if (repairResult.RelaunchRequired)
                {
                    var relaunchExitCode = await RelaunchWithCurrentArgumentsAsync(args, CancellationToken.None);
                    if (relaunchExitCode is null)
                    {
                        ConsoleOutput.Error("Startup repair installed a newer binary, but copilotd could not relaunch the requested command automatically.");
                        return 1;
                    }

                    return relaunchExitCode.Value;
                }
            }

            var rootCommand = new RootCommand("copilotd - GitHub issue dispatch daemon for Copilot CLI");

            rootCommand.Subcommands.Add(InitCommand.Create(services));
            rootCommand.Subcommands.Add(ConfigCommand.Create(services));
            rootCommand.Subcommands.Add(RulesCommand.Create(services));
            rootCommand.Subcommands.Add(RunCommand.Create(services));
            rootCommand.Subcommands.Add(StartCommand.Create(services));
            rootCommand.Subcommands.Add(StopCommand.Create(services));
            rootCommand.Subcommands.Add(StatusCommand.Create(services));
            rootCommand.Subcommands.Add(SessionCommand.Create(services));
            rootCommand.Subcommands.Add(UpdateCommand.Create(services));
            rootCommand.Subcommands.Add(ShutdownInstanceCommand.Create(services));

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
        serviceCollection.AddSingleton<RepoPathResolver>();
        serviceCollection.AddSingleton<ProcessManager>();
        serviceCollection.AddSingleton<GitHubRemoteSessionUrlResolver>();
        serviceCollection.AddSingleton<GhCliService>();
        serviceCollection.AddSingleton<CopilotCliService>();
        serviceCollection.AddSingleton<ReconciliationEngine>();
        serviceCollection.AddSingleton<GitHubReleaseService>();
        serviceCollection.AddSingleton<ProvenanceVerifier>();
        serviceCollection.AddSingleton<RuntimeContext>();
        serviceCollection.AddSingleton<UpdateService>();

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

    private static bool ShouldRunStartupRepair(string[] args, RuntimeContext runtimeContext, StateStore stateStore)
    {
        if (!runtimeContext.SupportsInPlaceSelfUpdate())
            return false;

        if (stateStore.IsLockHeld())
            return false;

        if (!IsStartupRepairCommand(args))
            return false;

        return true;
    }

    private static bool IsStartupRepairCommand(string[] args)
    {
        if (args.Any(IsHelpOrVersionArgument))
            return false;

        var command = args.FirstOrDefault(a => !a.StartsWith('-'));
        return string.Equals(command, "run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "start", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHelpOrVersionArgument(string arg)
        => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "-?", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase)
           || string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase);

    private static async Task<int?> RelaunchWithCurrentArgumentsAsync(string[] args, CancellationToken ct)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return null;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return null;

            using (process)
            {
                await process.WaitForExitAsync(ct);
                return process.ExitCode;
            }
        }
        catch
        {
            return null;
        }
    }
}
