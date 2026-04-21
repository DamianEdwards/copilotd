using System.CommandLine;
using System.Reflection;
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
                var copilotTrust = services.GetRequiredService<CopilotTrustService>();
                var stateStore = services.GetRequiredService<StateStore>();
                var repoResolver = services.GetRequiredService<RepoPathResolver>();
                var state = stateStore.LoadState();

                // ── Phase 1: Dependencies & Auth ──────────────────────────
                AnsiConsole.Write(new Rule("[bold blue]Dependencies & Authentication[/]").LeftJustified());
                AnsiConsole.WriteLine();

                var copilotdVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                    ?? "unknown";
                AnsiConsole.MarkupLine($"  [blue]copilotd[/] v{Markup.Escape(copilotdVersion)}");
                AnsiConsole.WriteLine();

                // gh CLI
                var ghVersion = ghCli.GetVersion();
                if (ghVersion is null)
                {
                    AnsiConsole.MarkupLine("  [red]✗[/] gh CLI — not found");
                    ConsoleOutput.Info("  Install it from: https://cli.github.com/");
                    return 1;
                }
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(ghVersion)}");

                // copilot CLI
                var copilotVersion = copilotCli.GetVersion();
                if (copilotVersion is null)
                {
                    AnsiConsole.MarkupLine("  [red]✗[/] copilot CLI — not found");
                    ConsoleOutput.Info("  Install it from: https://docs.github.com/copilot/how-tos/copilot-cli");
                    return 1;
                }
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(copilotVersion)}");

                // Auth
                var authResult = ghCli.CheckAuth();
                if (!authResult.IsLoggedIn)
                {
                    AnsiConsole.MarkupLine("  [red]✗[/] gh CLI — not authenticated");
                    ConsoleOutput.Info("  Run 'gh auth login' first.");
                    return 1;
                }
                var username = authResult.Username;
                AnsiConsole.MarkupLine($"  [green]✓[/] Authenticated as [bold]{Markup.Escape(username ?? "unknown")}[/]");

                // copilot CLI uses gh auth — verify it's responsive (not a true auth check)
                if (!copilotCli.IsLoggedIn())
                {
                    AnsiConsole.MarkupLine("  [red]✗[/] copilot CLI — not responding");
                    ConsoleOutput.Info("  Reinstall from: https://docs.github.com/copilot/how-tos/copilot-cli");
                    return 1;
                }
                AnsiConsole.MarkupLine("  [green]✓[/] copilot CLI ready (uses gh authentication)");

                AnsiConsole.WriteLine();

                // Load existing config or start fresh
                var config = stateStore.LoadConfig();
                config.CurrentUser = username;

                // ── Phase 2: Repo Home ────────────────────────────────────
                AnsiConsole.Write(new Rule("[bold blue]Repository Home[/]").LeftJustified());
                AnsiConsole.WriteLine();

                var examplePath = OperatingSystem.IsWindows() ? @"C:\source" : "~/repos";
                AnsiConsole.MarkupLine("[grey]This is the root directory where your GitHub repos are cloned.[/]");
                var repoHomePrompt = new TextPrompt<string>($"Enter repo home directory (e.g., {Markup.Escape(examplePath)}):");
                if (config.RepoHome is not null)
                    repoHomePrompt.DefaultValue(config.RepoHome);
                var repoHome = AnsiConsole.Prompt(repoHomePrompt);

                if (string.IsNullOrWhiteSpace(repoHome))
                {
                    ConsoleOutput.Error("Repo home directory is required.");
                    return 1;
                }

                // Expand ~ to home directory
                if (repoHome.StartsWith('~'))
                {
                    repoHome = CopilotdPaths.ExpandUserProfile(repoHome);
                }

                config.RepoHome = Path.GetFullPath(repoHome);
                AnsiConsole.WriteLine();

                // ── Phase 3: Global Config ────────────────────────────────
                AnsiConsole.Write(new Rule("[bold blue]Global Settings[/]").LeftJustified());
                AnsiConsole.WriteLine();

                // Max concurrent sessions
                AnsiConsole.MarkupLine("[grey]Maximum number of copilot sessions running in parallel.[/]");
                config.MaxInstances = AnsiConsole.Prompt(
                    new TextPrompt<int>("Max concurrent sessions:")
                        .DefaultValue(config.MaxInstances)
                        .Validate(v => v > 0 ? ValidationResult.Success() : ValidationResult.Error("Must be a positive integer")));
                AnsiConsole.WriteLine();

                // Default model
                AnsiConsole.MarkupLine("[grey]Optional default model for all sessions (e.g., claude-sonnet-4, o4-mini).[/]");
                AnsiConsole.MarkupLine("[grey]Leave empty to use the copilot CLI default. Can be overridden per rule.[/]");
                var modelInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("Default model:")
                        .DefaultValue(config.DefaultModel ?? "")
                        .AllowEmpty());
                config.DefaultModel = string.IsNullOrWhiteSpace(modelInput) ? null : modelInput.Trim();
                AnsiConsole.WriteLine();

                // ── Phase 4: Default Rule Setup ───────────────────────────
                AnsiConsole.Write(new Rule("[bold blue]Default Dispatch Rule[/]").LeftJustified());
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]The default rule controls which issues copilotd will pick up and dispatch.[/]");
                AnsiConsole.WriteLine();

                var existingRule = config.Rules.GetValueOrDefault(CopilotdConfig.DefaultRuleName);

                // Author filtering
                const string AuthorAny = "Any author";
                const string AuthorWriteAccess = "Only authors with write access to the repo";
                var authorOnlyMe = $"Only me ({Markup.Escape(username ?? "unknown")})";

                // Determine existing selection for re-run and build choices with it first
                var authorDefault = existingRule?.AuthorMode switch
                {
                    AuthorMode.Allowed when existingRule.Authors.Count == 1
                        && string.Equals(existingRule.Authors[0], username, StringComparison.OrdinalIgnoreCase)
                        => authorOnlyMe,
                    AuthorMode.WriteAccess => AuthorWriteAccess,
                    _ => AuthorAny
                };

                // Build choices with the current/default selection first so Spectre highlights it
                var authorChoices = new List<string> { authorDefault };
                foreach (var c in new[] { AuthorAny, authorOnlyMe, AuthorWriteAccess })
                {
                    if (c != authorDefault) authorChoices.Add(c);
                }

                // Don't offer "Only me" if username couldn't be determined
                if (username is null)
                    authorChoices.Remove(authorOnlyMe);

                var authorChoice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Who can create issues that copilotd will dispatch?")
                        .AddChoices(authorChoices)
                        .HighlightStyle(new Style(Color.Blue)));
                AnsiConsole.MarkupLine($"  Issue authors: [blue]{Markup.Escape(authorChoice)}[/]");

                AuthorMode authorMode;
                List<string> authors = [];
                if (authorChoice == authorOnlyMe)
                {
                    authorMode = AuthorMode.Allowed;
                    authors = [username!];
                }
                else if (authorChoice == AuthorWriteAccess)
                {
                    authorMode = AuthorMode.WriteAccess;
                }
                else
                {
                    authorMode = AuthorMode.Any;
                }
                AnsiConsole.WriteLine();

                // Labels
                AnsiConsole.MarkupLine("[grey]Issues must have ALL of these labels to be dispatched.[/]");
                var existingLabels = existingRule?.Labels.Count > 0
                    ? string.Join(", ", existingRule.Labels)
                    : "copilotd";
                var labelsInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("Required label(s) (comma-separated):")
                        .DefaultValue(existingLabels));
                var labels = labelsInput
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                AnsiConsole.MarkupLine($"  Labels: [blue]{Markup.Escape(string.Join(", ", labels))}[/]");
                AnsiConsole.WriteLine();

                // Yolo / tool permissions
                AnsiConsole.MarkupLine("[grey]Yolo mode skips all confirmation prompts (implies --allow-all-tools and --allow-all-urls).[/]");
                var existingYolo = existingRule?.Yolo ?? false;
                var yolo = AnsiConsole.Confirm("Enable yolo mode?", existingYolo);
                AnsiConsole.MarkupLine($"  Yolo mode: [blue]{(yolo ? "yes" : "no")}[/]");

                bool allowAllTools = true;
                bool allowAllUrls = false;
                if (!yolo)
                {
                    AnsiConsole.MarkupLine("[grey]Allow copilot to use all available tools without prompting.[/]");
                    allowAllTools = AnsiConsole.Confirm("Allow all tools?", existingRule?.AllowAllTools ?? true);
                    AnsiConsole.MarkupLine($"  Allow all tools: [blue]{(allowAllTools ? "yes" : "no")}[/]");
                    AnsiConsole.MarkupLine("[grey]Allow copilot to access any URL without prompting.[/]");
                    allowAllUrls = AnsiConsole.Confirm("Allow all URLs?", existingRule?.AllowAllUrls ?? false);
                    AnsiConsole.MarkupLine($"  Allow all URLs: [blue]{(allowAllUrls ? "yes" : "no")}[/]");
                }
                AnsiConsole.WriteLine();

                // Rule model override
                AnsiConsole.MarkupLine("[grey]Optionally override the default model for sessions matching this rule.[/]");
                var existingRuleModel = existingRule?.Model ?? "";
                var ruleModelInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("Model override for default rule (empty to inherit global):")
                        .DefaultValue(existingRuleModel)
                        .AllowEmpty());
                var ruleModel = string.IsNullOrWhiteSpace(ruleModelInput) ? null : ruleModelInput.Trim();
                AnsiConsole.MarkupLine($"  Model override: [blue]{Markup.Escape(ruleModel ?? "(inherit global)")}[/]");
                AnsiConsole.WriteLine();

                // Build the default rule
                var defaultRule = existingRule ?? new DispatchRule();
                defaultRule.User = username;
                defaultRule.Labels = labels;
                defaultRule.AuthorMode = authorMode;
                defaultRule.Authors = authors;
                defaultRule.Yolo = yolo;
                defaultRule.AllowAllTools = yolo || allowAllTools;
                defaultRule.AllowAllUrls = yolo || allowAllUrls;
                defaultRule.Model = ruleModel;
                config.Rules[CopilotdConfig.DefaultRuleName] = defaultRule;

                // ── Phase 5: Repo Selection ───────────────────────────────
                AnsiConsole.Write(new Rule("[bold blue]Repository Selection[/]").LeftJustified());
                AnsiConsole.WriteLine();

                ConsoleOutput.Info("Fetching your repositories...");
                var repos = ghCli.ListRepos();

                if (repos.Count == 0)
                {
                    ConsoleOutput.Warning("No repositories found. You can add repos to rules later.");
                }
                else
                {
                    // Check which repos are cloned locally under RepoHome (single scan, not per-repo)
                    var cloneStatus = repoResolver.BuildCloneStatusMap(repos, config);

                    var clonedCount = cloneStatus.Values.Count(v => v);
                    AnsiConsole.MarkupLine($"[grey]Found {clonedCount} of {repos.Count} repos cloned under {Markup.Escape(config.RepoHome)}[/]");
                    AnsiConsole.MarkupLine("[grey]Only cloned repos can be dispatched — repos are not auto-cloned.[/]");
                    AnsiConsole.WriteLine();

                    var repoPrompt = new MultiSelectionPrompt<string>()
                            .Title("Select repositories to watch ([green]cloned[/] repos will dispatch, [red]not cloned[/] repos will be skipped until cloned):")
                            .PageSize(15)
                            .MoreChoicesText("[grey](Move up/down, space to select, enter to confirm)[/]")
                            .InstructionsText("[grey](Press space to toggle, enter to accept)[/]")
                            .UseConverter(r => cloneStatus.GetValueOrDefault(r)
                                ? $"{Markup.Escape(r)} [green](cloned)[/]"
                                : $"{Markup.Escape(r)} [red](not cloned)[/]")
                            .AddChoices(repos);

                    // Pre-select previously chosen repos on re-run
                    if (existingRule?.Repos is { Count: > 0 } existingRepos)
                    {
                        foreach (var repo in existingRepos.Where(r => repos.Contains(r, StringComparer.OrdinalIgnoreCase)))
                            repoPrompt.Select(repo);
                    }

                    var selected = AnsiConsole.Prompt(repoPrompt);

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

                    defaultRule.Repos = selected;
                    CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                        copilotTrust,
                        repoResolver,
                        copilotCli,
                        config,
                        state,
                        selected);
                }
                AnsiConsole.WriteLine();

                // ── Phase 6: Save & Summary ───────────────────────────────
                stateStore.SaveConfig(config);

                AnsiConsole.Write(new Rule("[bold green]Configuration Saved[/]").LeftJustified());
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[grey]Config stored in: {Markup.Escape(stateStore.ConfigDir)}[/]");
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CopilotdPaths.HomeEnvVar)))
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(CopilotdPaths.HomeEnvVar)} is overriding the default ~/.copilotd location.[/]");
                AnsiConsole.WriteLine();

                // Summary table
                var summaryTable = new Table().Border(TableBorder.Rounded).ShowRowSeparators();
                summaryTable.AddColumn(new TableColumn("[bold]Setting[/]").NoWrap());
                summaryTable.AddColumn(new TableColumn("[bold]Value[/]"));

                summaryTable.AddRow("[blue]Repo home[/]", Markup.Escape(config.RepoHome));
                summaryTable.AddRow("[blue]Max concurrent sessions[/]", Markup.Escape(config.MaxInstances.ToString()));
                summaryTable.AddRow("[blue]Default model[/]", Markup.Escape(config.DefaultModel ?? "(copilot default)"));
                summaryTable.AddRow("", "");
                summaryTable.AddRow("[bold blue]Default Rule[/]", "");
                summaryTable.AddRow("[blue]  Assignee[/]", Markup.Escape(defaultRule.User ?? "*"));
                summaryTable.AddRow("[blue]  Author filter[/]", Markup.Escape(FormatAuthorMode(defaultRule)));
                summaryTable.AddRow("[blue]  Labels[/]", Markup.Escape(string.Join(", ", defaultRule.Labels)));
                summaryTable.AddRow("[blue]  Permissions[/]", Markup.Escape(FormatPermissions(defaultRule)));
                summaryTable.AddRow("[blue]  Model override[/]", Markup.Escape(defaultRule.Model ?? "(inherit global)"));
                summaryTable.AddRow("[blue]  Repos[/]", Markup.Escape(
                    defaultRule.Repos.Count > 0 ? string.Join(", ", defaultRule.Repos) : "(none)"));

                AnsiConsole.Write(summaryTable);
                AnsiConsole.WriteLine();

                // Next steps
                AnsiConsole.Write(new Rule("[bold blue]Next Steps[/]").LeftJustified());
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("  [blue]copilotd run[/]                                           Start the daemon");
                AnsiConsole.MarkupLine("  [blue]copilotd run --interval 15 --log-level debug[/]           Start with verbose logging");
                AnsiConsole.MarkupLine("  [blue]copilotd rules update Default --add-repo org/repo[/]      Add another repo to the default rule");
                AnsiConsole.MarkupLine("  [blue]copilotd rules add MyRule --label bug --yolo[/]           Create a new dispatch rule");
                AnsiConsole.MarkupLine("  [blue]copilotd config --set max_instances=5[/]                  Change concurrency limit");
                AnsiConsole.MarkupLine("  [blue]copilotd config --set default_model=claude-sonnet-4[/]    Set the default model");
                AnsiConsole.MarkupLine("  [blue]copilotd status[/]                                        Check daemon health and sessions");
                AnsiConsole.WriteLine();

                return 0;
            }, logger);
        });

        return command;
    }

    private static string FormatAuthorMode(DispatchRule rule)
    {
        return rule.AuthorMode switch
        {
            AuthorMode.Allowed => string.Join(", ", rule.Authors),
            AuthorMode.WriteAccess => "(write access only)",
            _ => "(any)",
        };
    }

    private static string FormatPermissions(DispatchRule rule)
    {
        if (rule.Yolo) return "--yolo";
        var parts = new List<string>();
        if (rule.AllowAllTools) parts.Add("--allow-all-tools");
        if (rule.AllowAllUrls) parts.Add("--allow-all-urls");
        return parts.Count > 0 ? string.Join(", ", parts) : "(defaults)";
    }
}
