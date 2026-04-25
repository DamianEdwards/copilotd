using System.CommandLine;
using System.Reflection;
using System.Text;
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
                stateStore.EnsureMachineIdentifier(ct);

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

                ConsoleOutput.Info("Fetching owned repositories and checking local clones...");
                var ownedRepos = ghCli.ListOwnedRepos();
                var clonedRepoSlugs = repoResolver.ListClonedRepoSlugs(config);
                var ownedRepoSlugs = new HashSet<string>(
                    ownedRepos.Select(repo => repo.NameWithOwner),
                    StringComparer.OrdinalIgnoreCase);

                var repos = new List<AccessibleGitHubRepo>(ownedRepos);
                if (!string.IsNullOrWhiteSpace(username))
                {
                    foreach (var repoSlug in clonedRepoSlugs.OrderBy(slug => slug, StringComparer.OrdinalIgnoreCase))
                    {
                        if (ownedRepoSlugs.Contains(repoSlug))
                            continue;

                        if (!ghCli.HasWriteAccess(repoSlug, username))
                            continue;

                        repos.Add(new AccessibleGitHubRepo
                        {
                            NameWithOwner = repoSlug,
                            AccessKind = GitHubRepoAccessKind.WriteAccess,
                        });
                    }
                }

                MergeExistingRepos(repos, existingRule?.Repos ?? [], username);
                repos = repos
                    .DistinctBy(repo => repo.NameWithOwner, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(repo => repo.NameWithOwner, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (repos.Count == 0)
                {
                    ConsoleOutput.Warning("No repositories found. You can add repos to rules later.");
                }
                else
                {
                    var cloneStatus = BuildCloneStatusMap(repos, clonedRepoSlugs);

                    var clonedCount = cloneStatus.Values.Count(v => v);
                    var ownedCount = repos.Count(repo => repo.AccessKind == GitHubRepoAccessKind.Owned);
                    var writeAccessCount = repos.Count - ownedCount;
                    AnsiConsole.MarkupLine(
                        $"[grey]Loaded {repos.Count} repos to start with: {ownedCount} owned and {writeAccessCount} write-access clones or saved selections.[/]");
                    AnsiConsole.MarkupLine(
                        $"[grey]{clonedCount} are cloned under {Markup.Escape(config.RepoHome)} and can dispatch immediately.[/]");
                    AnsiConsole.MarkupLine("[grey]Additional write-access repos that are not cloned can be loaded on demand from the group picker.[/]");
                    AnsiConsole.MarkupLine("[grey]Use the group picker to edit one slice at a time. Only cloned repos can dispatch — repos are not auto-cloned.[/]");
                    AnsiConsole.WriteLine();

                    var selected = PromptForRepoSelection(repos, clonedRepoSlugs, existingRule?.Repos ?? [], ghCli, username);

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

    private static void MergeExistingRepos(
        List<AccessibleGitHubRepo> repos,
        IReadOnlyList<string> existingRepos,
        string? username)
    {
        var knownRepos = new HashSet<string>(
            repos.Select(repo => repo.NameWithOwner),
            StringComparer.OrdinalIgnoreCase);

        foreach (var repoSlug in existingRepos)
        {
            if (!knownRepos.Add(repoSlug))
                continue;

            var owner = repoSlug.Split('/', 2)[0];
            repos.Add(new AccessibleGitHubRepo
            {
                NameWithOwner = repoSlug,
                AccessKind = !string.IsNullOrWhiteSpace(username)
                    && string.Equals(owner, username, StringComparison.OrdinalIgnoreCase)
                    ? GitHubRepoAccessKind.Owned
                    : GitHubRepoAccessKind.WriteAccess,
            });
        }
    }

    private static Dictionary<string, bool> BuildCloneStatusMap(
        IReadOnlyList<AccessibleGitHubRepo> repos,
        IReadOnlySet<string> clonedRepoSlugs)
        => repos.ToDictionary(
            repo => repo.NameWithOwner,
            repo => clonedRepoSlugs.Contains(repo.NameWithOwner),
            StringComparer.OrdinalIgnoreCase);

    private static List<string> PromptForRepoSelection(
        List<AccessibleGitHubRepo> repos,
        HashSet<string> clonedRepoSlugs,
        IReadOnlyList<string> existingRepos,
        GhCliService ghCli,
        string? username)
    {
        var selectedRepos = new HashSet<string>(existingRepos, StringComparer.OrdinalIgnoreCase);
        var additionalWriteAccessReposLoaded = false;

        while (true)
        {
            var cloneStatus = BuildCloneStatusMap(repos, clonedRepoSlugs);
            var menuOptions = BuildRepoSelectionMenuOptions(repos, cloneStatus, selectedRepos, additionalWriteAccessReposLoaded);

            var selectedMenuOption = AnsiConsole.Prompt(
                new SelectionPrompt<RepoSelectionMenuOption>()
                    .Title($"Choose a repository group to edit ({selectedRepos.Count} selected):")
                    .PageSize(8)
                    .EnableSearch()
                    .SearchPlaceholderText("Type to search groups...")
                    .MoreChoicesText("[grey](Use up/down to navigate, type to search, enter to open)[/]")
                    .UseConverter(option => option.DisplayText)
                    .AddChoices(menuOptions));

            switch (selectedMenuOption.Action)
            {
                case RepoSelectionMenuAction.EditGroup:
                    EditRepoSelectionGroup(selectedMenuOption, repos, cloneStatus, selectedRepos);
                    AnsiConsole.MarkupLine($"[grey]{selectedRepos.Count} repo(s) selected so far.[/]");
                    AnsiConsole.WriteLine();
                    break;

                case RepoSelectionMenuAction.OpenWriteAccessNotCloned:
                    HandleWriteAccessNotClonedFlow(
                        repos,
                        clonedRepoSlugs,
                        selectedRepos,
                        ghCli,
                        username,
                        ref additionalWriteAccessReposLoaded);
                    AnsiConsole.MarkupLine($"[grey]{selectedRepos.Count} repo(s) selected so far.[/]");
                    AnsiConsole.WriteLine();
                    break;

                case RepoSelectionMenuAction.ReviewSelected:
                    ReviewSelectedRepos(repos, cloneStatus, selectedRepos);
                    AnsiConsole.WriteLine();
                    break;

                case RepoSelectionMenuAction.Done:
                    return selectedRepos
                        .OrderBy(repo => repo, StringComparer.OrdinalIgnoreCase)
                        .ToList();
            }
        }
    }

    private static List<RepoSelectionMenuOption> BuildRepoSelectionMenuOptions(
        IReadOnlyList<AccessibleGitHubRepo> repos,
        IReadOnlyDictionary<string, bool> cloneStatus,
        IReadOnlySet<string> selectedRepos,
        bool additionalWriteAccessReposLoaded)
    {
        List<RepoSelectionMenuOption> options = [];

        foreach (var (accessKind, isCloned, label) in RepoSelectionGroups)
        {
            if (accessKind == GitHubRepoAccessKind.WriteAccess && !isCloned)
            {
                var loadedReposInGroup = repos
                    .Where(repo => repo.AccessKind == accessKind && cloneStatus.GetValueOrDefault(repo.NameWithOwner) == isCloned)
                    .ToList();
                var loadedSelectedCount = loadedReposInGroup.Count(repo => selectedRepos.Contains(repo.NameWithOwner));
                options.Add(new RepoSelectionMenuOption
                {
                    Action = RepoSelectionMenuAction.OpenWriteAccessNotCloned,
                    AccessKind = accessKind,
                    IsCloned = isCloned,
                    Label = label,
                    DisplayText = additionalWriteAccessReposLoaded
                        ? $"{label} — {loadedReposInGroup.Count} repo(s), {loadedSelectedCount} selected"
                        : loadedReposInGroup.Count > 0
                            ? $"{label} — {loadedReposInGroup.Count} loaded, {loadedSelectedCount} selected (search or load all)"
                            : $"{label} — search GitHub or load all",
                });
                continue;
            }

            var reposInGroup = repos
                .Where(repo => repo.AccessKind == accessKind && cloneStatus.GetValueOrDefault(repo.NameWithOwner) == isCloned)
                .ToList();
            var selectedCount = reposInGroup.Count(repo => selectedRepos.Contains(repo.NameWithOwner));
            options.Add(new RepoSelectionMenuOption
            {
                Action = RepoSelectionMenuAction.EditGroup,
                AccessKind = accessKind,
                IsCloned = isCloned,
                Label = label,
                DisplayText = $"{label} — {reposInGroup.Count} repo(s), {selectedCount} selected",
            });
        }

        options.Add(new RepoSelectionMenuOption
        {
            Action = RepoSelectionMenuAction.ReviewSelected,
            DisplayText = $"Review selected repos — {selectedRepos.Count} selected",
        });

        options.Add(new RepoSelectionMenuOption
        {
            Action = RepoSelectionMenuAction.Done,
            DisplayText = "Done",
        });

        return options;
    }

    private static void HandleWriteAccessNotClonedFlow(
        List<AccessibleGitHubRepo> repos,
        HashSet<string> clonedRepoSlugs,
        HashSet<string> selectedRepos,
        GhCliService ghCli,
        string? username,
        ref bool additionalWriteAccessReposLoaded)
    {
        while (true)
        {
            var cloneStatus = BuildCloneStatusMap(repos, clonedRepoSlugs);
            var loadedRepos = repos
                .Where(repo => repo.AccessKind == GitHubRepoAccessKind.WriteAccess && !cloneStatus.GetValueOrDefault(repo.NameWithOwner))
                .OrderBy(repo => repo.NameWithOwner, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selectedCount = loadedRepos.Count(repo => selectedRepos.Contains(repo.NameWithOwner));

            var menuOptions = new List<WriteAccessNotClonedMenuOption>
            {
                new()
                {
                    Action = WriteAccessNotClonedMenuAction.SearchByTerm,
                    DisplayText = "Search GitHub by term (recommended)",
                },
            };

            if (loadedRepos.Count > 0)
            {
                menuOptions.Add(new WriteAccessNotClonedMenuOption
                {
                    Action = WriteAccessNotClonedMenuAction.EditLoadedResults,
                    DisplayText = $"Edit loaded results — {loadedRepos.Count} repo(s), {selectedCount} selected",
                });
            }

            if (!additionalWriteAccessReposLoaded)
            {
                menuOptions.Add(new WriteAccessNotClonedMenuOption
                {
                    Action = WriteAccessNotClonedMenuAction.LoadAll,
                    DisplayText = "Load all from GitHub (slow)",
                });
            }

            menuOptions.Add(new WriteAccessNotClonedMenuOption
            {
                Action = WriteAccessNotClonedMenuAction.Back,
                DisplayText = "Back",
            });

            var selectedOption = AnsiConsole.Prompt(
                new SelectionPrompt<WriteAccessNotClonedMenuOption>()
                    .Title("Choose how to find write-access repos that are not cloned:")
                    .PageSize(6)
                    .UseConverter(option => option.DisplayText)
                    .AddChoices(menuOptions));

            switch (selectedOption.Action)
            {
                case WriteAccessNotClonedMenuAction.SearchByTerm:
                    SearchWriteAccessNotClonedRepos(repos, clonedRepoSlugs, selectedRepos, ghCli, username);
                    break;

                case WriteAccessNotClonedMenuAction.EditLoadedResults:
                    EditRepoSelectionGroup(
                        new RepoSelectionMenuOption
                        {
                            Action = RepoSelectionMenuAction.EditGroup,
                            AccessKind = GitHubRepoAccessKind.WriteAccess,
                            IsCloned = false,
                            Label = "Write access (not cloned)",
                        },
                        repos,
                        cloneStatus,
                        selectedRepos);
                    break;

                case WriteAccessNotClonedMenuAction.LoadAll:
                    additionalWriteAccessReposLoaded = true;
                    LoadAdditionalWriteAccessRepos(repos, ghCli, username);
                    break;

                case WriteAccessNotClonedMenuAction.Back:
                    return;
            }
        }
    }

    private static void SearchWriteAccessNotClonedRepos(
        List<AccessibleGitHubRepo> repos,
        HashSet<string> clonedRepoSlugs,
        HashSet<string> selectedRepos,
        GhCliService ghCli,
        string? username)
    {
        var searchTerm = PromptInlineSearchTerm();
        if (string.IsNullOrWhiteSpace(searchTerm))
            return;

        List<AccessibleGitHubRepo> searchResults = [];
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Searching GitHub...", _ =>
            {
                searchResults = ghCli.SearchAccessibleRepos(searchTerm, username);
            });

        var matches = searchResults
            .Where(repo => repo.AccessKind == GitHubRepoAccessKind.WriteAccess && !clonedRepoSlugs.Contains(repo.NameWithOwner))
            .OrderBy(repo => repo.NameWithOwner, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            ConsoleOutput.Warning($"No write-access repos found for '{searchTerm}' that are not already cloned.");
            return;
        }

        MergeDiscoveredRepos(repos, matches);
        var matchCloneStatus = BuildCloneStatusMap(matches, clonedRepoSlugs);

        var resultPrompt = new MultiSelectionPrompt<AccessibleGitHubRepo>()
            .Title($"Search results for {Markup.Escape(searchTerm)}:")
            .PageSize(15)
            .NotRequired()
            .MoreChoicesText("[grey](Move up/down to see more repos)[/]")
            .InstructionsText("[grey](Press space to toggle, enter to save these results)[/]")
            .UseConverter(repo => FormatRepoChoice(repo, matchCloneStatus))
            .AddChoices(matches);

        foreach (var repo in matches.Where(repo => selectedRepos.Contains(repo.NameWithOwner)))
            resultPrompt.Select(repo);

        var selectedMatches = AnsiConsole.Prompt(resultPrompt);

        foreach (var repo in matches)
            selectedRepos.Remove(repo.NameWithOwner);

        foreach (var repo in selectedMatches)
            selectedRepos.Add(repo.NameWithOwner);
    }

    private static string PromptInlineSearchTerm()
    {
        const string prompt = "Search term or owner/repo (empty to go back): ";
        var input = new StringBuilder();

        RenderInlinePrompt(prompt, input.ToString());

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    ClearInlinePrompt();
                    return input.ToString();

                case ConsoleKey.Backspace:
                    if (input.Length > 0)
                        input.Length--;
                    break;

                case ConsoleKey.Escape:
                    input.Clear();
                    ClearInlinePrompt();
                    return "";

                default:
                    if (!char.IsControl(key.KeyChar))
                        input.Append(key.KeyChar);
                    break;
            }

            RenderInlinePrompt(prompt, input.ToString());
        }
    }

    private static void RenderInlinePrompt(string prompt, string value)
    {
        var text = prompt + value;
        var width = Math.Max(GetConsoleWidth() - 1, text.Length);
        Console.Write('\r');
        Console.Write(text);
        if (width > text.Length)
            Console.Write(new string(' ', width - text.Length));
        Console.Write('\r');
        Console.Write(text);
    }

    private static void ClearInlinePrompt()
    {
        var width = Math.Max(GetConsoleWidth() - 1, 1);
        Console.Write('\r');
        Console.Write(new string(' ', width));
        Console.Write('\r');
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Console.BufferWidth;
        }
        catch
        {
            return 120;
        }
    }

    private static void MergeDiscoveredRepos(List<AccessibleGitHubRepo> repos, IReadOnlyList<AccessibleGitHubRepo> discoveredRepos)
    {
        var existingRepoSlugs = new HashSet<string>(
            repos.Select(repo => repo.NameWithOwner),
            StringComparer.OrdinalIgnoreCase);

        foreach (var repo in discoveredRepos)
        {
            if (existingRepoSlugs.Add(repo.NameWithOwner))
                repos.Add(repo);
        }

        repos.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.NameWithOwner, right.NameWithOwner));
    }

    private static void LoadAdditionalWriteAccessRepos(
        List<AccessibleGitHubRepo> repos,
        GhCliService ghCli,
        string? username)
    {
        ConsoleOutput.Info("Fetching additional write-access repositories from GitHub. This can take a while for large org memberships...");

        List<AccessibleGitHubRepo> accessibleRepos = [];
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Loading write-access repositories...", _ =>
            {
                accessibleRepos = ghCli.ListAccessibleRepos(username);
            });

        MergeDiscoveredRepos(
            repos,
            accessibleRepos.Where(repo => repo.AccessKind == GitHubRepoAccessKind.WriteAccess).ToList());
    }

    private static void EditRepoSelectionGroup(
        RepoSelectionMenuOption menuOption,
        IReadOnlyList<AccessibleGitHubRepo> repos,
        IReadOnlyDictionary<string, bool> cloneStatus,
        HashSet<string> selectedRepos)
    {
        if (menuOption.AccessKind is null || menuOption.IsCloned is null || string.IsNullOrWhiteSpace(menuOption.Label))
            return;

        var groupRepos = repos
            .Where(repo => repo.AccessKind == menuOption.AccessKind
                && cloneStatus.GetValueOrDefault(repo.NameWithOwner) == menuOption.IsCloned.Value)
            .OrderBy(repo => repo.NameWithOwner, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groupRepos.Count == 0)
        {
            ConsoleOutput.Info($"No repositories found in {menuOption.Label}.");
            return;
        }

        var groupPrompt = new MultiSelectionPrompt<AccessibleGitHubRepo>()
            .Title($"Select repositories in {Markup.Escape(menuOption.Label)}:")
            .PageSize(15)
            .NotRequired()
            .MoreChoicesText("[grey](Move up/down to see more repos)[/]")
            .InstructionsText("[grey](Press space to toggle, enter to save this group and return)[/]")
            .UseConverter(repo => FormatRepoChoice(repo, cloneStatus))
            .AddChoices(groupRepos);

        foreach (var repo in groupRepos.Where(repo => selectedRepos.Contains(repo.NameWithOwner)))
            groupPrompt.Select(repo);

        var selectedInGroup = AnsiConsole.Prompt(groupPrompt);

        foreach (var repo in groupRepos)
            selectedRepos.Remove(repo.NameWithOwner);

        foreach (var repo in selectedInGroup)
            selectedRepos.Add(repo.NameWithOwner);
    }

    private static void ReviewSelectedRepos(
        IReadOnlyList<AccessibleGitHubRepo> repos,
        IReadOnlyDictionary<string, bool> cloneStatus,
        HashSet<string> selectedRepos)
    {
        var currentSelection = repos
            .Where(repo => selectedRepos.Contains(repo.NameWithOwner))
            .OrderBy(repo => repo.NameWithOwner, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currentSelection.Count == 0)
        {
            ConsoleOutput.Warning("No repositories selected yet.");
            return;
        }

        var reviewPrompt = new MultiSelectionPrompt<AccessibleGitHubRepo>()
            .Title("Review selected repositories:")
            .PageSize(15)
            .NotRequired()
            .MoreChoicesText("[grey](Move up/down to see more repos)[/]")
            .InstructionsText("[grey](Press space to remove or restore, enter to return)[/]")
            .UseConverter(repo => FormatRepoChoice(repo, cloneStatus))
            .AddChoices(currentSelection);

        foreach (var repo in currentSelection)
            reviewPrompt.Select(repo);

        var reviewedSelection = AnsiConsole.Prompt(reviewPrompt);

        selectedRepos.Clear();
        foreach (var repo in reviewedSelection)
            selectedRepos.Add(repo.NameWithOwner);
    }

    private static string FormatRepoChoice(AccessibleGitHubRepo repo, IReadOnlyDictionary<string, bool> cloneStatus)
    {
        var cloneLabel = cloneStatus.GetValueOrDefault(repo.NameWithOwner)
            ? "[green](cloned)[/]"
            : "[red](not cloned)[/]";
        var accessLabel = repo.AccessKind == GitHubRepoAccessKind.Owned
            ? "[blue](owned)[/]"
            : "[yellow](write access)[/]";
        return $"{Markup.Escape(repo.NameWithOwner)} {cloneLabel} {accessLabel}";
    }

    private static readonly (GitHubRepoAccessKind AccessKind, bool IsCloned, string Label)[] RepoSelectionGroups =
    [
        (GitHubRepoAccessKind.Owned, true, "Owned (cloned)"),
        (GitHubRepoAccessKind.WriteAccess, true, "Write access (cloned)"),
        (GitHubRepoAccessKind.Owned, false, "Owned (not cloned)"),
        (GitHubRepoAccessKind.WriteAccess, false, "Write access (not cloned)"),
    ];

    private enum RepoSelectionMenuAction
    {
        EditGroup,
        OpenWriteAccessNotCloned,
        ReviewSelected,
        Done,
    }

    private enum WriteAccessNotClonedMenuAction
    {
        SearchByTerm,
        EditLoadedResults,
        LoadAll,
        Back,
    }

    private sealed class RepoSelectionMenuOption
    {
        public RepoSelectionMenuAction Action { get; init; }
        public GitHubRepoAccessKind? AccessKind { get; init; }
        public bool? IsCloned { get; init; }
        public string? Label { get; init; }
        public string DisplayText { get; init; } = "";

        public override string ToString() => DisplayText;
    }

    private sealed class WriteAccessNotClonedMenuOption
    {
        public WriteAccessNotClonedMenuAction Action { get; init; }
        public string DisplayText { get; init; } = "";

        public override string ToString() => DisplayText;
    }
}
