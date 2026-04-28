using System.CommandLine;
using System.CommandLine.Help;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Copilotd.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class RulesCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("rules", "Manage dispatch rules");
        command.Aliases.Add("rule");
        command.Subcommands.Add(CreateList(services));
        command.Subcommands.Add(CreateAdd(services));
        command.Subcommands.Add(CreateUpdate(services));
        command.Subcommands.Add(CreateDelete(services));

        // Default to list behavior when no subcommand is specified
        var repoOption = new Option<string?>("--repo") { Description = "Filter rules by repository" };
        var assigneeOption = new Option<string?>("--assignee") { Description = "Filter rules by assignee condition", Arity = ArgumentArity.ZeroOrOne };
        command.Options.Add(repoOption);
        command.Options.Add(assigneeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                new HelpAction().Invoke(parseResult);
                Console.WriteLine();

                var stateStore = services.GetRequiredService<StateStore>();
                var config = stateStore.LoadConfig();
                var repoFilter = parseResult.GetValue(repoOption);
                var assigneeFilter = parseResult.GetValue(assigneeOption);
                var assigneeFlagPresent = parseResult.GetResult(assigneeOption) is not null;

                return RenderRulesList(config, repoFilter, assigneeFilter, assigneeFlagPresent);
            }, logger);
        });

        return command;
    }

    private static Command CreateList(IServiceProvider services)
    {
        var command = new Command("list", "List dispatch rules");
        var repoOption = new Option<string?>("--repo") { Description = "Filter rules by repository" };
        var assigneeOption = new Option<string?>("--assignee") { Description = "Filter rules by assignee condition", Arity = ArgumentArity.ZeroOrOne };
        command.Options.Add(repoOption);
        command.Options.Add(assigneeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var config = stateStore.LoadConfig();
                var repoFilter = parseResult.GetValue(repoOption);
                var assigneeFilter = parseResult.GetValue(assigneeOption);
                var assigneeFlagPresent = parseResult.GetResult(assigneeOption) is not null;

                return RenderRulesList(config, repoFilter, assigneeFilter, assigneeFlagPresent);
            }, logger);
        });

        return command;
    }

    private static int RenderRulesList(CopilotdConfig config, string? repoFilter, string? assigneeFilter, bool assigneeFlagPresent)
    {
        var issueRuleCount = config.IssueRules.Count;
        var pullRequestRuleCount = config.PullRequestRules.Count;
        var issueRules = config.IssueRules.AsEnumerable();
        var pullRequestRules = config.PullRequestRules.AsEnumerable();

        if (repoFilter is not null)
        {
            issueRules = issueRules.Where(r => r.Value.Repos.Contains(repoFilter, StringComparer.OrdinalIgnoreCase));
            pullRequestRules = pullRequestRules.Where(r => r.Value.Repos.Contains(repoFilter, StringComparer.OrdinalIgnoreCase));
        }

        if (assigneeFlagPresent)
        {
            if (assigneeFilter is not null)
            {
                issueRules = issueRules.Where(r => string.Equals(r.Value.Assignee, assigneeFilter, StringComparison.OrdinalIgnoreCase));
                pullRequestRules = pullRequestRules.Where(r => string.Equals(r.Value.Assignee, assigneeFilter, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                issueRules = issueRules.Where(r => r.Value.Assignee is not null);
                pullRequestRules = pullRequestRules.Where(r => r.Value.Assignee is not null);
            }
        }

        var issueRuleList = issueRules.ToList();
        var pullRequestRuleList = pullRequestRules.ToList();

        AnsiConsole.MarkupLine("[bold]Issue dispatch rules[/]");
        if (issueRuleList.Count == 0)
        {
            RenderNoRulesMessage("issue", issueRuleCount == 0, "copilotd rules add <name> --kind issue");
        }
        else
        {
            var table = new Table();
            table.ShowRowSeparators = true;
            table.AddColumn(new TableColumn("[bold]Name[/]"));
            table.AddColumn(new TableColumn("[bold]Assignee[/]"));
            table.AddColumn(new TableColumn("[bold]Authors[/]"));
            table.AddColumn(new TableColumn("[bold]Labels[/]"));
            table.AddColumn(new TableColumn("[bold]Milestone[/]"));
            table.AddColumn(new TableColumn("[bold]Type[/]"));
            table.AddColumn(new TableColumn("[bold]Repos[/]"));
            table.AddColumn(new TableColumn("[bold]Launch Options[/]"));

            foreach (var kvp in issueRuleList)
            {
                var name = kvp.Key;
                var issueRule = kvp.Value;
                table.AddRow(
                    Markup.Escape(name),
                    Markup.Escape(issueRule.Assignee ?? "*"),
                    Markup.Escape(FormatAuthorMode(issueRule)),
                    Markup.Escape(string.Join(", ", issueRule.Labels)),
                    Markup.Escape(issueRule.Milestone ?? "*"),
                    Markup.Escape(issueRule.Type ?? "*"),
                    Markup.Escape(string.Join(", ", issueRule.Repos)),
                    Markup.Escape(FormatLaunchOptions(issueRule)));
            }

            AnsiConsole.Write(table);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Pull request dispatch rules[/]");
        if (pullRequestRuleList.Count == 0)
        {
            RenderNoRulesMessage("pull request", pullRequestRuleCount == 0, "copilotd rules add <name> --kind pr");
        }
        else
        {
            var prTable = new Table();
            prTable.ShowRowSeparators = true;
            prTable.AddColumn(new TableColumn("[bold]Name[/]"));
            prTable.AddColumn(new TableColumn("[bold]Assignee[/]"));
            prTable.AddColumn(new TableColumn("[bold]Authors[/]"));
            prTable.AddColumn(new TableColumn("[bold]Labels[/]"));
            prTable.AddColumn(new TableColumn("[bold]Base[/]"));
            prTable.AddColumn(new TableColumn("[bold]Draft[/]"));
            prTable.AddColumn(new TableColumn("[bold]Review[/]"));
            prTable.AddColumn(new TableColumn("[bold]Branch[/]"));
            prTable.AddColumn(new TableColumn("[bold]Repos[/]"));
            prTable.AddColumn(new TableColumn("[bold]Launch Options[/]"));

            foreach (var kvp in pullRequestRuleList)
            {
                var name = kvp.Key;
                var pullRequestRule = kvp.Value;
                prTable.AddRow(
                    Markup.Escape(name),
                    Markup.Escape(pullRequestRule.Assignee ?? "*"),
                    Markup.Escape(FormatAuthorMode(pullRequestRule)),
                    Markup.Escape(string.Join(", ", pullRequestRule.Labels)),
                    Markup.Escape(pullRequestRule.BaseBranch ?? "*"),
                    Markup.Escape(pullRequestRule.Draft?.ToString() ?? "*"),
                    Markup.Escape(pullRequestRule.ReviewDecision ?? "*"),
                    Markup.Escape(pullRequestRule.BranchStrategy.ToString()),
                    Markup.Escape(string.Join(", ", pullRequestRule.Repos)),
                    Markup.Escape(FormatLaunchOptions(pullRequestRule)));
            }

            AnsiConsole.Write(prTable);
        }
        return 0;
    }

    private static void RenderNoRulesMessage(string dispatchType, bool noneDefined, string addCommand)
    {
        var reason = noneDefined
            ? $"No {dispatchType} dispatch rules are currently defined."
            : $"No {dispatchType} dispatch rules match the current filters.";
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(reason)} Run [blue]{Markup.Escape(addCommand)}[/] to define one.[/]");
    }

    private static string FormatLaunchOptions(DispatchRuleOptions dispatchRule)
    {
        var parts = new List<string>();

        if (dispatchRule.Yolo)
        {
            parts.Add("--yolo");
        }
        else
        {
            if (dispatchRule.AllowAllTools) parts.Add("--allow-all-tools");
            if (dispatchRule.AllowAllUrls) parts.Add("--allow-all-urls");
        }

        if (dispatchRule.Model is not null)
            parts.Add($"--model={dispatchRule.Model}");

        return parts.Count > 0 ? string.Join(", ", parts) : "(defaults)";
    }

    private static string FormatAuthorMode(IssueDispatchRule issueRule)
    {
        return issueRule.AuthorMode switch
        {
            AuthorMode.Allowed => string.Join(", ", issueRule.Authors),
            AuthorMode.WriteAccess => "(write access)",
            _ => "*",
        };
    }

    private static string FormatAuthorMode(PullRequestDispatchRule pullRequestRule)
    {
        return pullRequestRule.AuthorMode switch
        {
            AuthorMode.Allowed => string.Join(", ", pullRequestRule.Authors),
            AuthorMode.WriteAccess => "(write access)",
            _ => "*",
        };
    }

    private static Command CreateAdd(IServiceProvider services)
    {
        var command = new Command("add", "Add a new dispatch rule");
        command.Aliases.Add("new");
        command.Aliases.Add("create");
        var nameArg = new Argument<string>("name");
        var kindOption = new Option<string>("--kind") { Description = "Rule kind: issue (default) or pr" };
        var assigneeOption = new Option<string?>("--assignee") { Description = "Assignee condition" };
        var labelOption = new Option<string[]>("--label") { Description = "Label condition (can be specified multiple times)", AllowMultipleArgumentsPerToken = true };
        var milestoneOption = new Option<string?>("--milestone") { Description = "Milestone condition" };
        var typeOption = new Option<string?>("--type") { Description = "Issue type condition" };
        var yoloOption = new Option<bool>("--yolo") { Description = "Pass --yolo to copilot (implies --allow-all-tools and --allow-all-urls)" };
        var allowAllToolsOption = new Option<bool?>("--allow-all-tools") { Description = "Pass --allow-all-tools to copilot (default: true)" };
        var allowAllUrlsOption = new Option<bool?>("--allow-all-urls") { Description = "Pass --allow-all-urls to copilot (default: false)" };
        var promptOption = new Option<string?>("--prompt") { Description = "Extra prompt for this rule" };
        var modelOption = new Option<string?>("--model") { Description = "Model to use for sessions triggered by this rule (overrides global default_model)" };
        var customPromptOption = new Option<string?>("--custom-prompt") { Description = "Per-rule custom prompt (appended to or overrides global custom prompt)" };
        var customPromptModeOption = new Option<string?>("--custom-prompt-mode") { Description = "How rule custom prompt interacts with global: 'append' (default) or 'override'" };
        var repoOption = new Option<string[]>("--repo") { Description = "Repository to add (can be specified multiple times)", AllowMultipleArgumentsPerToken = true };
        var addAuthorOption = new Option<string[]>("--add-author") { Description = "Add an allowed issue author (can be specified multiple times)", AllowMultipleArgumentsPerToken = true };
        var writeOnlyAuthorsOption = new Option<bool>("--write-only-authors") { Description = "Only dispatch issues from authors with write access to the repo" };
        var anyAuthorOption = new Option<bool>("--any-author") { Description = "Allow issues from any author (default)" };
        var baseOption = new Option<string?>("--base") { Description = "PR base branch condition (PR rules only)" };
        var headOption = new Option<string?>("--head") { Description = "PR head branch condition (PR rules only)" };
        var headRepoOption = new Option<string?>("--head-repo") { Description = "PR head repo condition (PR rules only)" };
        var draftOption = new Option<bool?>("--draft") { Description = "PR draft-state condition (PR rules only)" };
        var reviewDecisionOption = new Option<string?>("--review-decision") { Description = "PR review decision condition (PR rules only)" };
        var branchStrategyOption = new Option<string?>("--branch-strategy") { Description = "PR branch strategy: source-branch, child-branch, or read-only" };

        command.Arguments.Add(nameArg);
        command.Options.Add(kindOption);
        command.Options.Add(assigneeOption);
        command.Options.Add(labelOption);
        command.Options.Add(milestoneOption);
        command.Options.Add(typeOption);
        command.Options.Add(yoloOption);
        command.Options.Add(allowAllToolsOption);
        command.Options.Add(allowAllUrlsOption);
        command.Options.Add(promptOption);
        command.Options.Add(modelOption);
        command.Options.Add(customPromptOption);
        command.Options.Add(customPromptModeOption);
        command.Options.Add(repoOption);
        command.Options.Add(addAuthorOption);
        command.Options.Add(writeOnlyAuthorsOption);
        command.Options.Add(anyAuthorOption);
        command.Options.Add(baseOption);
        command.Options.Add(headOption);
        command.Options.Add(headRepoOption);
        command.Options.Add(draftOption);
        command.Options.Add(reviewDecisionOption);
        command.Options.Add(branchStrategyOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var copilotCli = services.GetRequiredService<CopilotCliService>();
                var copilotTrust = services.GetRequiredService<CopilotTrustService>();
                var repoResolver = services.GetRequiredService<RepoPathResolver>();
                var stateStore = services.GetRequiredService<StateStore>();
                var config = stateStore.LoadConfig();
                var state = stateStore.LoadState();

                var name = parseResult.GetValue(nameArg)!;

                if (config.IssueRules.ContainsKey(name) || config.PullRequestRules.ContainsKey(name))
                {
                    ConsoleOutput.Error($"Rule '{name}' already exists. Use 'rules update' to modify it.");
                    return 1;
                }

                var kind = parseResult.GetValue(kindOption);
                var isPrRule = string.Equals(kind, "pr", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, "pull-request", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, "pullrequest", StringComparison.OrdinalIgnoreCase);

                if (!isPrRule && !string.IsNullOrWhiteSpace(kind) && !string.Equals(kind, "issue", StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleOutput.Error("Invalid --kind. Use 'issue' or 'pr'.");
                    return 1;
                }

                if (isPrRule)
                {
                    if (parseResult.GetResult(milestoneOption) is not null || parseResult.GetResult(typeOption) is not null)
                    {
                        ConsoleOutput.Error("--milestone and --type apply only to issue rules.");
                        return 1;
                    }

                    var branchStrategyValue = parseResult.GetValue(branchStrategyOption);
                    if (!TryParseBranchStrategy(branchStrategyValue, out var branchStrategy))
                    {
                        ConsoleOutput.Error("Invalid --branch-strategy. Use 'source-branch', 'child-branch', or 'read-only'.");
                        return 1;
                    }

                    var pullRequestRule = new PullRequestDispatchRule
                    {
                        Assignee = parseResult.GetValue(assigneeOption),
                        Labels = [.. parseResult.GetValue(labelOption) ?? []],
                        BaseBranch = parseResult.GetValue(baseOption),
                        HeadBranch = parseResult.GetValue(headOption),
                        HeadRepo = parseResult.GetValue(headRepoOption),
                        Draft = parseResult.GetValue(draftOption),
                        ReviewDecision = parseResult.GetValue(reviewDecisionOption),
                        BranchStrategy = branchStrategy,
                        Yolo = parseResult.GetValue(yoloOption),
                        AllowAllTools = parseResult.GetValue(allowAllToolsOption) ?? true,
                        AllowAllUrls = parseResult.GetValue(allowAllUrlsOption) ?? false,
                        Model = string.IsNullOrWhiteSpace(parseResult.GetValue(modelOption)) ? null : parseResult.GetValue(modelOption),
                        ExtraPrompt = parseResult.GetValue(promptOption),
                        CustomPrompt = parseResult.GetValue(customPromptOption),
                        Repos = [.. parseResult.GetValue(repoOption) ?? []],
                        Authors = [.. parseResult.GetValue(addAuthorOption) ?? []],
                    };

                    if (parseResult.GetValue(writeOnlyAuthorsOption))
                        pullRequestRule.AuthorMode = AuthorMode.WriteAccess;
                    else if (pullRequestRule.Authors.Count > 0)
                        pullRequestRule.AuthorMode = AuthorMode.Allowed;

                    var prModeValue = parseResult.GetValue(customPromptModeOption);
                    if (prModeValue is not null)
                    {
                        if (!TryParsePromptMode(prModeValue, out var prMode))
                        {
                            ConsoleOutput.Error("Invalid --custom-prompt-mode. Use 'append' or 'override'.");
                            return 1;
                        }
                        pullRequestRule.CustomPromptMode = prMode;
                    }

                    config.PullRequestRules[name] = pullRequestRule;
                    CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                        copilotTrust,
                        repoResolver,
                        copilotCli,
                        config,
                        state,
                        pullRequestRule.Repos);
                    stateStore.SaveConfig(config);
                    ConsoleOutput.Success($"Pull request rule '{name}' added.");
                    return 0;
                }

                var issueRule = new IssueDispatchRule
                {
                    Assignee = parseResult.GetValue(assigneeOption),
                    Labels = [.. parseResult.GetValue(labelOption) ?? []],
                    Milestone = parseResult.GetValue(milestoneOption),
                    Type = parseResult.GetValue(typeOption),
                    Yolo = parseResult.GetValue(yoloOption),
                    AllowAllTools = parseResult.GetValue(allowAllToolsOption) ?? true,
                    AllowAllUrls = parseResult.GetValue(allowAllUrlsOption) ?? false,
                    Model = string.IsNullOrWhiteSpace(parseResult.GetValue(modelOption)) ? null : parseResult.GetValue(modelOption),
                    ExtraPrompt = parseResult.GetValue(promptOption),
                    CustomPrompt = parseResult.GetValue(customPromptOption),
                    Repos = [.. parseResult.GetValue(repoOption) ?? []],
                    Authors = [.. parseResult.GetValue(addAuthorOption) ?? []],
                };

                // Author mode: --write-only-authors wins over --add-author, --any-author is default
                if (parseResult.GetValue(writeOnlyAuthorsOption))
                {
                    issueRule.AuthorMode = AuthorMode.WriteAccess;
                }
                else if (issueRule.Authors.Count > 0)
                {
                    issueRule.AuthorMode = AuthorMode.Allowed;
                }
                // else: AuthorMode.Any (default)

                var modeValue = parseResult.GetValue(customPromptModeOption);
                if (modeValue is not null)
                {
                    if (!TryParsePromptMode(modeValue, out var mode))
                    {
                        ConsoleOutput.Error("Invalid --custom-prompt-mode. Use 'append' or 'override'.");
                        return 1;
                    }
                    issueRule.CustomPromptMode = mode;
                }

                config.IssueRules[name] = issueRule;
                CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                    copilotTrust,
                    repoResolver,
                    copilotCli,
                    config,
                    state,
                    issueRule.Repos);
                stateStore.SaveConfig(config);
                ConsoleOutput.Success($"Rule '{name}' added.");
                return 0;
            }, logger);
        });

        return command;
    }

    private static Command CreateUpdate(IServiceProvider services)
    {
        var command = new Command("update", "Update an existing dispatch rule");
        command.Aliases.Add("edit");
        var nameArg = new Argument<string>("name");
        var assigneeOption = new Option<string?>("--assignee") { Description = "Update assignee condition" };
        var addLabelOption = new Option<string[]>("--add-label") { Description = "Add a label condition", AllowMultipleArgumentsPerToken = true };
        var deleteLabelOption = new Option<string[]>("--delete-label") { Description = "Remove a label condition", AllowMultipleArgumentsPerToken = true };
        var milestoneOption = new Option<string?>("--milestone") { Description = "Update milestone condition" };
        var typeOption = new Option<string?>("--type") { Description = "Update type condition" };
        var yoloOption = new Option<bool?>("--yolo") { Description = "Update yolo setting" };
        var allowAllToolsOption = new Option<bool?>("--allow-all-tools") { Description = "Update allow-all-tools setting" };
        var allowAllUrlsOption = new Option<bool?>("--allow-all-urls") { Description = "Update allow-all-urls setting" };
        var promptOption = new Option<string?>("--prompt") { Description = "Update extra prompt" };
        var modelOption = new Option<string?>("--model") { Description = "Update model (overrides global default_model)" };
        var customPromptOption = new Option<string?>("--custom-prompt") { Description = "Update per-rule custom prompt" };
        var customPromptModeOption = new Option<string?>("--custom-prompt-mode") { Description = "Update custom prompt mode: 'append' or 'override'" };
        var addRepoOption = new Option<string[]>("--add-repo") { Description = "Add a repository", AllowMultipleArgumentsPerToken = true };
        var deleteRepoOption = new Option<string[]>("--delete-repo") { Description = "Remove a repository", AllowMultipleArgumentsPerToken = true };
        var addAuthorOption = new Option<string[]>("--add-author") { Description = "Add an allowed issue author", AllowMultipleArgumentsPerToken = true };
        var deleteAuthorOption = new Option<string[]>("--delete-author") { Description = "Remove an allowed issue author", AllowMultipleArgumentsPerToken = true };
        var writeOnlyAuthorsOption = new Option<bool>("--write-only-authors") { Description = "Only dispatch issues from authors with write access to the repo" };
        var anyAuthorOption = new Option<bool>("--any-author") { Description = "Allow issues from any author (clears author list)" };
        var baseOption = new Option<string?>("--base") { Description = "Update PR base branch condition (PR rules only)" };
        var headOption = new Option<string?>("--head") { Description = "Update PR head branch condition (PR rules only)" };
        var headRepoOption = new Option<string?>("--head-repo") { Description = "Update PR head repo condition (PR rules only)" };
        var draftOption = new Option<bool?>("--draft") { Description = "Update PR draft-state condition (PR rules only)" };
        var reviewDecisionOption = new Option<string?>("--review-decision") { Description = "Update PR review decision condition (PR rules only)" };
        var branchStrategyOption = new Option<string?>("--branch-strategy") { Description = "Update PR branch strategy: source-branch, child-branch, or read-only" };

        command.Arguments.Add(nameArg);
        command.Options.Add(assigneeOption);
        command.Options.Add(addLabelOption);
        command.Options.Add(deleteLabelOption);
        command.Options.Add(milestoneOption);
        command.Options.Add(typeOption);
        command.Options.Add(yoloOption);
        command.Options.Add(allowAllToolsOption);
        command.Options.Add(allowAllUrlsOption);
        command.Options.Add(promptOption);
        command.Options.Add(modelOption);
        command.Options.Add(customPromptOption);
        command.Options.Add(customPromptModeOption);
        command.Options.Add(addRepoOption);
        command.Options.Add(deleteRepoOption);
        command.Options.Add(addAuthorOption);
        command.Options.Add(deleteAuthorOption);
        command.Options.Add(writeOnlyAuthorsOption);
        command.Options.Add(anyAuthorOption);
        command.Options.Add(baseOption);
        command.Options.Add(headOption);
        command.Options.Add(headRepoOption);
        command.Options.Add(draftOption);
        command.Options.Add(reviewDecisionOption);
        command.Options.Add(branchStrategyOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var copilotCli = services.GetRequiredService<CopilotCliService>();
                var copilotTrust = services.GetRequiredService<CopilotTrustService>();
                var repoResolver = services.GetRequiredService<RepoPathResolver>();
                var stateStore = services.GetRequiredService<StateStore>();
                var config = stateStore.LoadConfig();
                var state = stateStore.LoadState();

                var name = parseResult.GetValue(nameArg)!;

                if (!config.IssueRules.TryGetValue(name, out var issueRule))
                {
                    if (!config.PullRequestRules.TryGetValue(name, out var pullRequestRule))
                    {
                        ConsoleOutput.Error($"Rule '{name}' not found.");
                        return 1;
                    }

                    if (parseResult.GetResult(milestoneOption) is not null || parseResult.GetResult(typeOption) is not null)
                    {
                        ConsoleOutput.Error("--milestone and --type apply only to issue rules.");
                        return 1;
                    }

                    if (parseResult.GetResult(assigneeOption) is not null)
                        pullRequestRule.Assignee = parseResult.GetValue(assigneeOption);

                    var prAddLabels = parseResult.GetValue(addLabelOption) ?? [];
                    var prDeleteLabels = parseResult.GetValue(deleteLabelOption) ?? [];
                    foreach (var label in prDeleteLabels)
                        pullRequestRule.Labels.Remove(label);
                    foreach (var label in prAddLabels)
                    {
                        if (!pullRequestRule.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                            pullRequestRule.Labels.Add(label);
                    }

                    if (parseResult.GetResult(baseOption) is not null)
                        pullRequestRule.BaseBranch = parseResult.GetValue(baseOption);

                    if (parseResult.GetResult(headOption) is not null)
                        pullRequestRule.HeadBranch = parseResult.GetValue(headOption);

                    if (parseResult.GetResult(headRepoOption) is not null)
                        pullRequestRule.HeadRepo = parseResult.GetValue(headRepoOption);

                    if (parseResult.GetResult(draftOption) is not null)
                        pullRequestRule.Draft = parseResult.GetValue(draftOption);

                    if (parseResult.GetResult(reviewDecisionOption) is not null)
                        pullRequestRule.ReviewDecision = parseResult.GetValue(reviewDecisionOption);

                    if (parseResult.GetResult(branchStrategyOption) is not null)
                    {
                        if (!TryParseBranchStrategy(parseResult.GetValue(branchStrategyOption), out var branchStrategy))
                        {
                            ConsoleOutput.Error("Invalid --branch-strategy. Use 'source-branch', 'child-branch', or 'read-only'.");
                            return 1;
                        }
                        pullRequestRule.BranchStrategy = branchStrategy;
                    }

                    if (parseResult.GetResult(yoloOption) is not null)
                        pullRequestRule.Yolo = parseResult.GetValue(yoloOption) ?? false;

                    if (parseResult.GetResult(allowAllToolsOption) is not null)
                        pullRequestRule.AllowAllTools = parseResult.GetValue(allowAllToolsOption) ?? true;

                    if (parseResult.GetResult(allowAllUrlsOption) is not null)
                        pullRequestRule.AllowAllUrls = parseResult.GetValue(allowAllUrlsOption) ?? false;

                    if (parseResult.GetResult(promptOption) is not null)
                        pullRequestRule.ExtraPrompt = parseResult.GetValue(promptOption);

                    if (parseResult.GetResult(modelOption) is not null)
                    {
                        var modelValue = parseResult.GetValue(modelOption);
                        pullRequestRule.Model = string.IsNullOrWhiteSpace(modelValue) ? null : modelValue;
                    }

                    if (parseResult.GetResult(customPromptOption) is not null)
                        pullRequestRule.CustomPrompt = parseResult.GetValue(customPromptOption);

                    if (parseResult.GetResult(customPromptModeOption) is not null)
                    {
                        var modeValue = parseResult.GetValue(customPromptModeOption);
                        if (!TryParsePromptMode(modeValue, out var mode))
                        {
                            ConsoleOutput.Error("Invalid --custom-prompt-mode. Use 'append' or 'override'.");
                            return 1;
                        }
                        pullRequestRule.CustomPromptMode = mode;
                    }

                    var prAddRepos = parseResult.GetValue(addRepoOption) ?? [];
                    var prDeleteRepos = parseResult.GetValue(deleteRepoOption) ?? [];
                    foreach (var repo in prDeleteRepos)
                        pullRequestRule.Repos.RemoveAll(r => string.Equals(r, repo, StringComparison.OrdinalIgnoreCase));
                    foreach (var repo in prAddRepos)
                    {
                        if (!pullRequestRule.Repos.Contains(repo, StringComparer.OrdinalIgnoreCase))
                            pullRequestRule.Repos.Add(repo);
                    }

                    if (parseResult.GetValue(anyAuthorOption))
                    {
                        pullRequestRule.AuthorMode = AuthorMode.Any;
                        pullRequestRule.Authors.Clear();
                    }
                    else if (parseResult.GetValue(writeOnlyAuthorsOption))
                    {
                        pullRequestRule.AuthorMode = AuthorMode.WriteAccess;
                        pullRequestRule.Authors.Clear();
                    }

                    var prAddAuthors = parseResult.GetValue(addAuthorOption) ?? [];
                    var prDeleteAuthors = parseResult.GetValue(deleteAuthorOption) ?? [];
                    foreach (var author in prDeleteAuthors)
                        pullRequestRule.Authors.RemoveAll(a => string.Equals(a, author, StringComparison.OrdinalIgnoreCase));
                    foreach (var author in prAddAuthors)
                    {
                        if (!pullRequestRule.Authors.Contains(author, StringComparer.OrdinalIgnoreCase))
                            pullRequestRule.Authors.Add(author);
                    }

                    if (pullRequestRule.Authors.Count > 0 && pullRequestRule.AuthorMode == AuthorMode.Any)
                    {
                        pullRequestRule.AuthorMode = AuthorMode.Allowed;
                    }

                    if (prAddRepos.Length > 0)
                    {
                        CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                            copilotTrust,
                            repoResolver,
                            copilotCli,
                            config,
                            state,
                            prAddRepos);
                    }

                    stateStore.SaveConfig(config);
                    ConsoleOutput.Success($"Pull request rule '{name}' updated.");
                    return 0;
                }

                if (parseResult.GetResult(assigneeOption) is not null)
                    issueRule.Assignee = parseResult.GetValue(assigneeOption);

                var addLabels = parseResult.GetValue(addLabelOption) ?? [];
                var deleteLabels = parseResult.GetValue(deleteLabelOption) ?? [];
                foreach (var label in deleteLabels)
                    issueRule.Labels.Remove(label);
                foreach (var label in addLabels)
                {
                    if (!issueRule.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                        issueRule.Labels.Add(label);
                }

                if (parseResult.GetResult(milestoneOption) is not null)
                    issueRule.Milestone = parseResult.GetValue(milestoneOption);

                if (parseResult.GetResult(typeOption) is not null)
                    issueRule.Type = parseResult.GetValue(typeOption);

                if (parseResult.GetResult(yoloOption) is not null)
                    issueRule.Yolo = parseResult.GetValue(yoloOption) ?? false;

                if (parseResult.GetResult(allowAllToolsOption) is not null)
                    issueRule.AllowAllTools = parseResult.GetValue(allowAllToolsOption) ?? true;

                if (parseResult.GetResult(allowAllUrlsOption) is not null)
                    issueRule.AllowAllUrls = parseResult.GetValue(allowAllUrlsOption) ?? false;

                if (parseResult.GetResult(promptOption) is not null)
                    issueRule.ExtraPrompt = parseResult.GetValue(promptOption);

                if (parseResult.GetResult(modelOption) is not null)
                {
                    var modelValue = parseResult.GetValue(modelOption);
                    issueRule.Model = string.IsNullOrWhiteSpace(modelValue) ? null : modelValue;
                }

                if (parseResult.GetResult(customPromptOption) is not null)
                    issueRule.CustomPrompt = parseResult.GetValue(customPromptOption);

                if (parseResult.GetResult(customPromptModeOption) is not null)
                {
                    var modeValue = parseResult.GetValue(customPromptModeOption);
                    if (!TryParsePromptMode(modeValue, out var mode))
                    {
                        ConsoleOutput.Error("Invalid --custom-prompt-mode. Use 'append' or 'override'.");
                        return 1;
                    }
                    issueRule.CustomPromptMode = mode;
                }

                var addRepos = parseResult.GetValue(addRepoOption) ?? [];
                var deleteRepos = parseResult.GetValue(deleteRepoOption) ?? [];
                foreach (var repo in deleteRepos)
                    issueRule.Repos.RemoveAll(r => string.Equals(r, repo, StringComparison.OrdinalIgnoreCase));
                foreach (var repo in addRepos)
                {
                    if (!issueRule.Repos.Contains(repo, StringComparer.OrdinalIgnoreCase))
                        issueRule.Repos.Add(repo);
                }

                // Author mode updates: --any-author and --write-only-authors change the mode;
                // --add-author/--delete-author modify the allowed list (and imply Allowed mode)
                if (parseResult.GetValue(anyAuthorOption))
                {
                    issueRule.AuthorMode = AuthorMode.Any;
                    issueRule.Authors.Clear();
                }
                else if (parseResult.GetValue(writeOnlyAuthorsOption))
                {
                    issueRule.AuthorMode = AuthorMode.WriteAccess;
                    issueRule.Authors.Clear();
                }

                var addAuthors = parseResult.GetValue(addAuthorOption) ?? [];
                var deleteAuthors = parseResult.GetValue(deleteAuthorOption) ?? [];
                foreach (var author in deleteAuthors)
                    issueRule.Authors.RemoveAll(a => string.Equals(a, author, StringComparison.OrdinalIgnoreCase));
                foreach (var author in addAuthors)
                {
                    if (!issueRule.Authors.Contains(author, StringComparer.OrdinalIgnoreCase))
                        issueRule.Authors.Add(author);
                }

                // If authors were added and mode is still Any, switch to Allowed
                if (issueRule.Authors.Count > 0 && issueRule.AuthorMode == AuthorMode.Any)
                {
                    issueRule.AuthorMode = AuthorMode.Allowed;
                }

                if (addRepos.Length > 0)
                {
                    CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                        copilotTrust,
                        repoResolver,
                        copilotCli,
                        config,
                        state,
                        addRepos);
                }

                stateStore.SaveConfig(config);
                ConsoleOutput.Success($"Rule '{name}' updated.");
                return 0;
            }, logger);
        });

        return command;
    }

    private static Command CreateDelete(IServiceProvider services)
    {
        var command = new Command("delete", "Delete a dispatch rule");
        var nameArg = new Argument<string>("name");
        command.Arguments.Add(nameArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var config = stateStore.LoadConfig();

                var name = parseResult.GetValue(nameArg)!;

                if (string.Equals(name, CopilotdConfig.DefaultRuleName, StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleOutput.Error("The 'Default' rule cannot be deleted.");
                    return 1;
                }

                var removed = config.IssueRules.Remove(name);
                if (!removed)
                    removed = config.PullRequestRules.Remove(name);

                if (!removed)
                {
                    ConsoleOutput.Error($"Rule '{name}' not found.");
                    return 1;
                }

                stateStore.SaveConfig(config);
                ConsoleOutput.Success($"Rule '{name}' deleted.");
                return 0;
            }, logger);
        });

        return command;
    }

    private static bool TryParsePromptMode(string? value, out PromptMode mode)
    {
        mode = PromptMode.Append;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (string.Equals(value, "append", StringComparison.OrdinalIgnoreCase))
        {
            mode = PromptMode.Append;
            return true;
        }

        if (string.Equals(value, "override", StringComparison.OrdinalIgnoreCase))
        {
            mode = PromptMode.Override;
            return true;
        }

        return false;
    }

    private static bool TryParseBranchStrategy(string? value, out PullRequestBranchStrategy strategy)
    {
        strategy = PullRequestBranchStrategy.SourceBranch;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (string.Equals(value, "source-branch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "source", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, nameof(PullRequestBranchStrategy.SourceBranch), StringComparison.OrdinalIgnoreCase))
        {
            strategy = PullRequestBranchStrategy.SourceBranch;
            return true;
        }

        if (string.Equals(value, "child-branch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "child", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, nameof(PullRequestBranchStrategy.ChildBranch), StringComparison.OrdinalIgnoreCase))
        {
            strategy = PullRequestBranchStrategy.ChildBranch;
            return true;
        }

        if (string.Equals(value, "read-only", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "readonly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, nameof(PullRequestBranchStrategy.ReadOnly), StringComparison.OrdinalIgnoreCase))
        {
            strategy = PullRequestBranchStrategy.ReadOnly;
            return true;
        }

        return false;
    }
}
