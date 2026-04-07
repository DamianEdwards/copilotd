using System.CommandLine;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Copilotd.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class InitCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("init", "Initialize copilotd configuration (first-run setup)");

        command.SetAction(async (ParseResult _, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var ghCli = services.GetRequiredService<GhCliService>();
                var copilotCli = services.GetRequiredService<CopilotCliService>();
                var stateStore = services.GetRequiredService<StateStore>();

                // Check dependencies
                if (!ghCli.IsAvailable())
                {
                    ConsoleOutput.Error("gh CLI is not installed or not on PATH.");
                    ConsoleOutput.Info("Install it from: https://cli.github.com/");
                    return 1;
                }

                if (!copilotCli.IsAvailable())
                {
                    ConsoleOutput.Error("copilot CLI is not installed or not on PATH.");
                    ConsoleOutput.Info("Install it from: https://docs.github.com/copilot/how-tos/copilot-cli");
                    return 1;
                }

                // Check auth
                var authResult = ghCli.CheckAuth();
                if (!authResult.IsLoggedIn)
                {
                    ConsoleOutput.Error("gh CLI is not authenticated. Run 'gh auth login' first.");
                    return 1;
                }
                var username = authResult.Username;

                if (!copilotCli.IsLoggedIn())
                {
                    ConsoleOutput.Error("copilot CLI is not authenticated. Run 'copilot login' first.");
                    return 1;
                }

                ConsoleOutput.Success($"Authenticated as: {username ?? "unknown"}");

                // Load existing config or start fresh
                var config = stateStore.LoadConfig();
                config.CurrentUser = username;

                // Prompt for repo home directory
                var examplePath = OperatingSystem.IsWindows() ? @"C:\source" : "~/repos";
                var prompt = $"Enter the directory where your repos are cloned (e.g., {examplePath}):";
                var repoHome = config.RepoHome is not null
                    ? AnsiConsole.Ask(prompt, config.RepoHome)
                    : AnsiConsole.Ask<string>(prompt);

                if (string.IsNullOrWhiteSpace(repoHome))
                {
                    ConsoleOutput.Error("Repo home directory is required.");
                    return 1;
                }

                // Expand ~ to home directory
                if (repoHome.StartsWith('~'))
                {
                    repoHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        repoHome[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                config.RepoHome = Path.GetFullPath(repoHome);

                // List repos for selection
                ConsoleOutput.Info("Fetching your repositories...");
                var repos = ghCli.ListRepos();

                if (repos.Count == 0)
                {
                    ConsoleOutput.Warning("No repositories found. You can add repos to rules later.");
                }
                else
                {
                    // Check which repos are cloned locally under RepoHome (single scan, not per-repo)
                    var repoResolver = services.GetRequiredService<RepoPathResolver>();
                    var cloneStatus = repoResolver.BuildCloneStatusMap(repos, config);

                    var clonedCount = cloneStatus.Values.Count(v => v);
                    AnsiConsole.MarkupLine($"[grey]Found {clonedCount} of {repos.Count} repos cloned under {Markup.Escape(config.RepoHome)}[/]");
                    AnsiConsole.MarkupLine("[grey]Only cloned repos can be dispatched — repos are not auto-cloned.[/]");
                    AnsiConsole.WriteLine();

                    var selected = AnsiConsole.Prompt(
                        new MultiSelectionPrompt<string>()
                            .Title("Select repositories to watch ([green]cloned[/] repos will dispatch, [red]not cloned[/] repos will be skipped until cloned):")
                            .PageSize(15)
                            .MoreChoicesText("[grey](Move up/down, space to select, enter to confirm)[/]")
                            .InstructionsText("[grey](Press space to toggle, enter to accept)[/]")
                            .UseConverter(r => cloneStatus.GetValueOrDefault(r)
                                ? $"{Markup.Escape(r)} [green](cloned)[/]"
                                : $"{Markup.Escape(r)} [red](not cloned)[/]")
                            .AddChoices(repos));

                    var notClonedSelected = selected.Where(r => !cloneStatus.GetValueOrDefault(r)).ToList();
                    if (notClonedSelected.Count > 0)
                    {
                        AnsiConsole.WriteLine();
                        ConsoleOutput.Warning($"{notClonedSelected.Count} selected repo(s) are not cloned yet and will be skipped during dispatch:");
                        foreach (var repo in notClonedSelected)
                            AnsiConsole.MarkupLine($"  [yellow]• {Markup.Escape(repo)}[/]");
                        AnsiConsole.MarkupLine($"[grey]Clone them under {Markup.Escape(config.RepoHome)} to enable dispatching.[/]");
                    }

                    if (selected.Count == 0)
                    {
                        ConsoleOutput.Warning("No repos selected. You can add repos to rules later.");
                    }

                    // Create or update default rule
                    if (!config.Rules.TryGetValue(CopilotdConfig.DefaultRuleName, out var defaultRule))
                    {
                        defaultRule = new DispatchRule
                        {
                            User = username,
                            Labels = ["copilotd"],
                        };
                        config.Rules[CopilotdConfig.DefaultRuleName] = defaultRule;
                    }

                    defaultRule.Repos = selected;
                }

                // Ensure default rule exists even if no repos selected
                if (!config.Rules.ContainsKey(CopilotdConfig.DefaultRuleName))
                {
                    config.Rules[CopilotdConfig.DefaultRuleName] = new DispatchRule
                    {
                        User = username,
                        Labels = ["copilotd"],
                    };
                }

                stateStore.SaveConfig(config);
                stateStore.EnsurePromptFile();
                ConsoleOutput.Success("Configuration saved successfully!");
                ConsoleOutput.Info($"Config stored in: {stateStore.ConfigDir}");

                AnsiConsole.WriteLine();
                ConsoleOutput.Info("Next steps:");
                AnsiConsole.MarkupLine("  Use [blue]copilotd rules[/] to manage dispatch rules and the repos they apply to.");
                AnsiConsole.MarkupLine("  Run [blue]copilotd run[/] to start the monitoring loop.");

                return 0;
            }, logger);
        });

        return command;
    }
}
