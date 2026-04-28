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
    private const string DefaultPullRequestRuleName = "Default PR";

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

                // ── Phase 4: Dispatch Source Selection ─────────────────────
                AnsiConsole.Write(new Rule("[bold blue]Dispatch Sources[/]").LeftJustified());
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Choose which GitHub subjects copilotd should dispatch from by default.[/]");
                var dispatchSource = AnsiConsole.Prompt(
                    new SelectionPrompt<DispatchSourceSelection>()
                        .Title("What should copilotd dispatch from?")
                        .AddChoices(DispatchSourceSelection.Issues, DispatchSourceSelection.PullRequests, DispatchSourceSelection.Both)
                        .UseConverter(FormatDispatchSourceSelection)
                        .HighlightStyle(new Style(Color.Blue)));
                var configureIssueDispatch = dispatchSource is DispatchSourceSelection.Issues or DispatchSourceSelection.Both;
                var configurePullRequestDispatch = dispatchSource is DispatchSourceSelection.PullRequests or DispatchSourceSelection.Both;
                AnsiConsole.WriteLine();

                IssueDispatchRule? defaultIssueRule = null;
                PullRequestDispatchRule? defaultPullRequestRule = null;
                var existingIssueRule = config.IssueRules.GetValueOrDefault(CopilotdConfig.DefaultRuleName);

                if (!configureIssueDispatch && config.IssueRules.Remove(CopilotdConfig.DefaultRuleName))
                    ConsoleOutput.Info("Removed the default issue dispatch rule because issue dispatch was not selected.");
                if (!configurePullRequestDispatch && config.PullRequestRules.Remove(DefaultPullRequestRuleName))
                    ConsoleOutput.Info("Removed the default PR dispatch rule because PR dispatch was not selected.");

                if (configureIssueDispatch)
                {
                    // ── Phase 5a: Default Issue Dispatch Rule Setup ────────────
                    AnsiConsole.Write(new Rule("[bold blue]Default Issue Dispatch Rule[/]").LeftJustified());
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[grey]The default issue rule controls which issues copilotd will pick up and dispatch.[/]");
                    AnsiConsole.WriteLine();

                    // Author filtering
                    const string AuthorAny = "Any author";
                    const string AuthorWriteAccess = "Only authors with write access to the repo";
                    var authorOnlyMe = $"Only me ({Markup.Escape(username ?? "unknown")})";

                    // Determine existing selection for re-run and build choices with it first
                    var authorDefault = existingIssueRule?.AuthorMode switch
                    {
                        AuthorMode.Allowed when existingIssueRule.Authors.Count == 1
                            && string.Equals(existingIssueRule.Authors[0], username, StringComparison.OrdinalIgnoreCase)
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
                    var existingLabels = existingIssueRule?.Labels.Count > 0
                        ? string.Join(", ", existingIssueRule.Labels)
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
                    var existingYolo = existingIssueRule?.Yolo ?? false;
                    var yolo = AnsiConsole.Confirm("Enable yolo mode?", existingYolo);
                    AnsiConsole.MarkupLine($"  Yolo mode: [blue]{(yolo ? "yes" : "no")}[/]");

                    bool allowAllTools = true;
                    bool allowAllUrls = false;
                    if (!yolo)
                    {
                        AnsiConsole.MarkupLine("[grey]Allow copilot to use all available tools without prompting.[/]");
                        allowAllTools = AnsiConsole.Confirm("Allow all tools?", existingIssueRule?.AllowAllTools ?? true);
                        AnsiConsole.MarkupLine($"  Allow all tools: [blue]{(allowAllTools ? "yes" : "no")}[/]");
                        AnsiConsole.MarkupLine("[grey]Allow copilot to access any URL without prompting.[/]");
                        allowAllUrls = AnsiConsole.Confirm("Allow all URLs?", existingIssueRule?.AllowAllUrls ?? false);
                        AnsiConsole.MarkupLine($"  Allow all URLs: [blue]{(allowAllUrls ? "yes" : "no")}[/]");
                    }
                    AnsiConsole.WriteLine();

                    // Rule model override
                    AnsiConsole.MarkupLine("[grey]Optionally override the default model for sessions matching this rule.[/]");
                    var existingRuleModel = existingIssueRule?.Model ?? "";
                    var ruleModelInput = AnsiConsole.Prompt(
                        new TextPrompt<string>("Model override for default rule (empty to inherit global):")
                            .DefaultValue(existingRuleModel)
                            .AllowEmpty());
                    var ruleModel = string.IsNullOrWhiteSpace(ruleModelInput) ? null : ruleModelInput.Trim();
                    AnsiConsole.MarkupLine($"  Model override: [blue]{Markup.Escape(ruleModel ?? "(inherit global)")}[/]");
                    AnsiConsole.WriteLine();

                    // Build the default rule
                    defaultIssueRule = existingIssueRule ?? new IssueDispatchRule();
                    defaultIssueRule.Assignee = username;
                    defaultIssueRule.Labels = labels;
                    defaultIssueRule.AuthorMode = authorMode;
                    defaultIssueRule.Authors = authors;
                    defaultIssueRule.Yolo = yolo;
                    defaultIssueRule.AllowAllTools = yolo || allowAllTools;
                    defaultIssueRule.AllowAllUrls = yolo || allowAllUrls;
                    defaultIssueRule.Model = ruleModel;
                    config.IssueRules[CopilotdConfig.DefaultRuleName] = defaultIssueRule;
                }

                if (configurePullRequestDispatch)
                {
                    // ── Phase 5b: Default Pull Request Dispatch Rule Setup ─────
                    AnsiConsole.Write(new Rule("[bold blue]Default Pull Request Dispatch Rule[/]").LeftJustified());
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[grey]The default PR rule controls which pull requests copilotd will pick up and dispatch.[/]");
                    AnsiConsole.WriteLine();

                    var existingPullRequestRule = config.PullRequestRules.GetValueOrDefault(DefaultPullRequestRuleName);

                    const string PrAuthorAny = "Any author";
                    const string PrAuthorWriteAccess = "Only authors with write access to the repo";
                    var prAuthorOnlyMe = $"Only me ({Markup.Escape(username ?? "unknown")})";
                    var prAuthorDefault = existingPullRequestRule?.AuthorMode switch
                    {
                        AuthorMode.Allowed when existingPullRequestRule.Authors.Count == 1
                            && string.Equals(existingPullRequestRule.Authors[0], username, StringComparison.OrdinalIgnoreCase)
                            => prAuthorOnlyMe,
                        AuthorMode.WriteAccess => PrAuthorWriteAccess,
                        _ => PrAuthorAny
                    };

                    var prAuthorChoices = new List<string> { prAuthorDefault };
                    foreach (var c in new[] { PrAuthorAny, prAuthorOnlyMe, PrAuthorWriteAccess })
                    {
                        if (c != prAuthorDefault) prAuthorChoices.Add(c);
                    }

                    if (username is null)
                        prAuthorChoices.Remove(prAuthorOnlyMe);

                    var prAuthorChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Who can create pull requests that copilotd will dispatch?")
                            .AddChoices(prAuthorChoices)
                            .HighlightStyle(new Style(Color.Blue)));
                    AnsiConsole.MarkupLine($"  PR authors: [blue]{Markup.Escape(prAuthorChoice)}[/]");

                    AuthorMode prAuthorMode;
                    List<string> prAuthors = [];
                    if (prAuthorChoice == prAuthorOnlyMe)
                    {
                        prAuthorMode = AuthorMode.Allowed;
                        prAuthors = [username!];
                    }
                    else if (prAuthorChoice == PrAuthorWriteAccess)
                    {
                        prAuthorMode = AuthorMode.WriteAccess;
                    }
                    else
                    {
                        prAuthorMode = AuthorMode.Any;
                    }
                    AnsiConsole.WriteLine();

                    AnsiConsole.MarkupLine("[grey]Pull requests must have ALL of these labels to be dispatched. Leave empty for any label set.[/]");
                    var existingPrLabels = existingPullRequestRule?.Labels.Count > 0
                        ? string.Join(", ", existingPullRequestRule.Labels)
                        : "";
                    var prLabelsInput = AnsiConsole.Prompt(
                        new TextPrompt<string>("Required PR label(s) (comma-separated, empty for none):")
                            .DefaultValue(existingPrLabels)
                            .AllowEmpty());
                    var prLabels = prLabelsInput
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    AnsiConsole.MarkupLine($"  PR labels: [blue]{Markup.Escape(prLabels.Count > 0 ? string.Join(", ", prLabels) : "(none)")}[/]");
                    AnsiConsole.WriteLine();

                    AnsiConsole.MarkupLine("[grey]Optionally restrict PR dispatch to a target/base branch, e.g. main. Leave empty for any base branch.[/]");
                    var prBaseBranchInput = AnsiConsole.Prompt(
                        new TextPrompt<string>("PR base branch:")
                            .DefaultValue(existingPullRequestRule?.BaseBranch ?? "")
                            .AllowEmpty());
                    var prBaseBranch = string.IsNullOrWhiteSpace(prBaseBranchInput) ? null : prBaseBranchInput.Trim();
                    AnsiConsole.MarkupLine($"  PR base branch: [blue]{Markup.Escape(prBaseBranch ?? "(any)")}[/]");
                    AnsiConsole.WriteLine();

                    const string DraftAny = "Any draft state";
                    const string DraftOnly = "Draft PRs only";
                    const string ReadyOnly = "Ready PRs only";
                    var draftDefault = existingPullRequestRule?.Draft switch
                    {
                        true => DraftOnly,
                        false => ReadyOnly,
                        _ => DraftAny
                    };
                    var draftChoices = new List<string> { draftDefault };
                    foreach (var c in new[] { DraftAny, DraftOnly, ReadyOnly })
                    {
                        if (c != draftDefault) draftChoices.Add(c);
                    }
                    var draftChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Which PR draft state should be dispatched?")
                            .AddChoices(draftChoices)
                            .HighlightStyle(new Style(Color.Blue)));
                    bool? draft = draftChoice switch
                    {
                        DraftOnly => true,
                        ReadyOnly => false,
                        _ => null
                    };
                    AnsiConsole.MarkupLine($"  PR draft state: [blue]{Markup.Escape(draftChoice)}[/]");
                    AnsiConsole.WriteLine();

                    const string SourceBranch = "Update the PR source branch (same-repo PRs only)";
                    const string ChildBranch = "Create a new child branch from the PR";
                    const string ReadOnly = "Read-only review/validation";
                    var branchStrategyDefault = existingPullRequestRule?.BranchStrategy switch
                    {
                        PullRequestBranchStrategy.ChildBranch => ChildBranch,
                        PullRequestBranchStrategy.ReadOnly => ReadOnly,
                        _ => SourceBranch
                    };
                    var branchStrategyChoices = new List<string> { branchStrategyDefault };
                    foreach (var c in new[] { SourceBranch, ChildBranch, ReadOnly })
                    {
                        if (c != branchStrategyDefault) branchStrategyChoices.Add(c);
                    }
                    var branchStrategyChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("How should PR-dispatched sessions handle branches?")
                            .AddChoices(branchStrategyChoices)
                            .HighlightStyle(new Style(Color.Blue)));
                    var branchStrategy = branchStrategyChoice switch
                    {
                        ChildBranch => PullRequestBranchStrategy.ChildBranch,
                        ReadOnly => PullRequestBranchStrategy.ReadOnly,
                        _ => PullRequestBranchStrategy.SourceBranch
                    };
                    AnsiConsole.MarkupLine($"  PR branch strategy: [blue]{Markup.Escape(branchStrategyChoice)}[/]");
                    AnsiConsole.WriteLine();

                    AnsiConsole.MarkupLine("[grey]Yolo mode skips all confirmation prompts (implies --allow-all-tools and --allow-all-urls).[/]");
                    var existingPrYolo = existingPullRequestRule?.Yolo ?? false;
                    var prYolo = AnsiConsole.Confirm("Enable yolo mode for PR sessions?", existingPrYolo);
                    AnsiConsole.MarkupLine($"  Yolo mode: [blue]{(prYolo ? "yes" : "no")}[/]");

                    bool prAllowAllTools = true;
                    bool prAllowAllUrls = false;
                    if (!prYolo)
                    {
                        AnsiConsole.MarkupLine("[grey]Allow copilot to use all available tools without prompting.[/]");
                        prAllowAllTools = AnsiConsole.Confirm("Allow all tools for PR sessions?", existingPullRequestRule?.AllowAllTools ?? true);
                        AnsiConsole.MarkupLine($"  Allow all tools: [blue]{(prAllowAllTools ? "yes" : "no")}[/]");
                        AnsiConsole.MarkupLine("[grey]Allow copilot to access any URL without prompting.[/]");
                        prAllowAllUrls = AnsiConsole.Confirm("Allow all URLs for PR sessions?", existingPullRequestRule?.AllowAllUrls ?? false);
                        AnsiConsole.MarkupLine($"  Allow all URLs: [blue]{(prAllowAllUrls ? "yes" : "no")}[/]");
                    }
                    AnsiConsole.WriteLine();

                    AnsiConsole.MarkupLine("[grey]Optionally override the default model for PR sessions matching this rule.[/]");
                    var existingPrRuleModel = existingPullRequestRule?.Model ?? "";
                    var prRuleModelInput = AnsiConsole.Prompt(
                        new TextPrompt<string>("Model override for default PR rule (empty to inherit global):")
                            .DefaultValue(existingPrRuleModel)
                            .AllowEmpty());
                    var prRuleModel = string.IsNullOrWhiteSpace(prRuleModelInput) ? null : prRuleModelInput.Trim();
                    AnsiConsole.MarkupLine($"  Model override: [blue]{Markup.Escape(prRuleModel ?? "(inherit global)")}[/]");
                    AnsiConsole.WriteLine();

                    defaultPullRequestRule = existingPullRequestRule ?? new PullRequestDispatchRule();
                    defaultPullRequestRule.Labels = prLabels;
                    defaultPullRequestRule.BaseBranch = prBaseBranch;
                    defaultPullRequestRule.Draft = draft;
                    defaultPullRequestRule.AuthorMode = prAuthorMode;
                    defaultPullRequestRule.Authors = prAuthors;
                    defaultPullRequestRule.BranchStrategy = branchStrategy;
                    defaultPullRequestRule.Yolo = prYolo;
                    defaultPullRequestRule.AllowAllTools = prYolo || prAllowAllTools;
                    defaultPullRequestRule.AllowAllUrls = prYolo || prAllowAllUrls;
                    defaultPullRequestRule.Model = prRuleModel;
                    config.PullRequestRules[DefaultPullRequestRuleName] = defaultPullRequestRule;
                }

                // ── Phase 6: Repo Selection ───────────────────────────────
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

                var existingDefaultRuleRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (defaultIssueRule is not null)
                    existingDefaultRuleRepos.UnionWith(defaultIssueRule.Repos);
                if (defaultPullRequestRule is not null)
                    existingDefaultRuleRepos.UnionWith(defaultPullRequestRule.Repos);

                MergeExistingRepos(repos, existingDefaultRuleRepos.ToList(), username);
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

                    var selected = PromptForRepoSelection(repos, clonedRepoSlugs, existingDefaultRuleRepos.ToList(), ghCli, username);

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

                    if (defaultIssueRule is not null)
                        defaultIssueRule.Repos = selected;
                    if (defaultPullRequestRule is not null)
                        defaultPullRequestRule.Repos = selected;
                    CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                        copilotTrust,
                        repoResolver,
                        copilotCli,
                        config,
                        state,
                        selected);
                }
                AnsiConsole.WriteLine();

                // ── Phase 7: Save & Summary ───────────────────────────────
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
                if (defaultIssueRule is not null)
                {
                    summaryTable.AddRow("[bold blue]Default Issue Rule[/]", "");
                    summaryTable.AddRow("[blue]  Assignee[/]", Markup.Escape(defaultIssueRule.Assignee ?? "*"));
                    summaryTable.AddRow("[blue]  Author filter[/]", Markup.Escape(FormatAuthorMode(defaultIssueRule)));
                    summaryTable.AddRow("[blue]  Labels[/]", Markup.Escape(string.Join(", ", defaultIssueRule.Labels)));
                    summaryTable.AddRow("[blue]  Permissions[/]", Markup.Escape(FormatPermissions(defaultIssueRule)));
                    summaryTable.AddRow("[blue]  Model override[/]", Markup.Escape(defaultIssueRule.Model ?? "(inherit global)"));
                    summaryTable.AddRow("[blue]  Repos[/]", Markup.Escape(
                        defaultIssueRule.Repos.Count > 0 ? string.Join(", ", defaultIssueRule.Repos) : "(none)"));
                }

                if (defaultPullRequestRule is not null)
                {
                    summaryTable.AddRow("[bold blue]Default Pull Request Rule[/]", Markup.Escape(DefaultPullRequestRuleName));
                    summaryTable.AddRow("[blue]  Author filter[/]", Markup.Escape(FormatAuthorMode(defaultPullRequestRule)));
                    summaryTable.AddRow("[blue]  Labels[/]", Markup.Escape(defaultPullRequestRule.Labels.Count > 0 ? string.Join(", ", defaultPullRequestRule.Labels) : "(none)"));
                    summaryTable.AddRow("[blue]  Base branch[/]", Markup.Escape(defaultPullRequestRule.BaseBranch ?? "(any)"));
                    summaryTable.AddRow("[blue]  Draft state[/]", Markup.Escape(FormatDraftState(defaultPullRequestRule.Draft)));
                    summaryTable.AddRow("[blue]  Branch strategy[/]", Markup.Escape(defaultPullRequestRule.BranchStrategy.ToString()));
                    summaryTable.AddRow("[blue]  Permissions[/]", Markup.Escape(FormatPermissions(defaultPullRequestRule)));
                    summaryTable.AddRow("[blue]  Model override[/]", Markup.Escape(defaultPullRequestRule.Model ?? "(inherit global)"));
                    summaryTable.AddRow("[blue]  Repos[/]", Markup.Escape(
                        defaultPullRequestRule.Repos.Count > 0 ? string.Join(", ", defaultPullRequestRule.Repos) : "(none)"));
                }

                AnsiConsole.Write(summaryTable);
                AnsiConsole.WriteLine();

                // Next steps
                AnsiConsole.Write(new Rule("[bold blue]Next Steps[/]").LeftJustified());
                AnsiConsole.WriteLine();

                var nextStepsTable = new Table()
                    .Border(TableBorder.None)
                    .HideHeaders();
                nextStepsTable.AddColumn(new TableColumn("").NoWrap());
                nextStepsTable.AddColumn("");

                AddNextStep(nextStepsTable, "copilotd run", "Start the daemon");
                AddNextStep(nextStepsTable, "copilotd run --interval 15 --log-level debug", "Start with verbose logging");
                if (defaultIssueRule is not null)
                    AddNextStep(nextStepsTable, "copilotd rules update Default --add-repo org/repo", "Add a repo to the default issue rule");
                if (defaultPullRequestRule is not null)
                    AddNextStep(nextStepsTable, "copilotd rules update \"Default PR\" --add-repo org/repo", "Add a repo to the default PR rule");
                AddNextStep(nextStepsTable, "copilotd rules add MyRule --kind issue --label bug --yolo", "Create a new issue dispatch rule");
                AddNextStep(nextStepsTable, "copilotd rules add MyPrRule --kind pr --label review", "Create a new PR dispatch rule");
                AddNextStep(nextStepsTable, "copilotd config --set max_instances=5", "Change concurrency limit");
                AddNextStep(nextStepsTable, "copilotd config --set default_model=claude-sonnet-4", "Set the default model");
                AddNextStep(nextStepsTable, "copilotd status", "Check daemon health and sessions");
                AnsiConsole.Write(nextStepsTable);
                AnsiConsole.WriteLine();

                return 0;
            }, logger);
        });

        return command;
    }

    private static string FormatAuthorMode(IssueDispatchRule rule)
    {
        return rule.AuthorMode switch
        {
            AuthorMode.Allowed => string.Join(", ", rule.Authors),
            AuthorMode.WriteAccess => "(write access only)",
            _ => "(any)",
        };
    }

    private static void AddNextStep(Table table, string command, string description)
    {
        table.AddRow($"  [blue]{Markup.Escape(command)}[/]", Markup.Escape(description));
    }

    private static string FormatAuthorMode(PullRequestDispatchRule rule)
    {
        return rule.AuthorMode switch
        {
            AuthorMode.Allowed => string.Join(", ", rule.Authors),
            AuthorMode.WriteAccess => "(write access only)",
            _ => "(any)",
        };
    }

    private static string FormatDraftState(bool? draft)
    {
        return draft switch
        {
            true => "Draft PRs only",
            false => "Ready PRs only",
            _ => "(any)",
        };
    }

    private static string FormatDispatchSourceSelection(DispatchSourceSelection selection)
    {
        return selection switch
        {
            DispatchSourceSelection.Issues => "Issues",
            DispatchSourceSelection.PullRequests => "Pull requests",
            DispatchSourceSelection.Both => "Issues and pull requests",
            _ => selection.ToString(),
        };
    }

    private static string FormatPermissions(DispatchRuleOptions rule)
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

    private enum DispatchSourceSelection
    {
        Issues,
        PullRequests,
        Both,
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
