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
        var rules = config.Rules.AsEnumerable();

        if (repoFilter is not null)
            rules = rules.Where(r => r.Value.Repos.Contains(repoFilter, StringComparer.OrdinalIgnoreCase));

        if (assigneeFlagPresent)
        {
            if (assigneeFilter is not null)
                rules = rules.Where(r => string.Equals(r.Value.User, assigneeFilter, StringComparison.OrdinalIgnoreCase));
            else
                rules = rules.Where(r => r.Value.User is not null);
        }

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

        foreach (var kvp in rules)
        {
            var name = kvp.Key;
            var rule = kvp.Value;
            table.AddRow(
                Markup.Escape(name),
                Markup.Escape(rule.User ?? "*"),
                Markup.Escape(FormatAuthorMode(rule)),
                Markup.Escape(string.Join(", ", rule.Labels)),
                Markup.Escape(rule.Milestone ?? "*"),
                Markup.Escape(rule.Type ?? "*"),
                Markup.Escape(string.Join(", ", rule.Repos)),
                Markup.Escape(FormatLaunchOptions(rule)));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static string FormatLaunchOptions(DispatchRule rule)
    {
        var parts = new List<string>();

        if (rule.Yolo)
        {
            parts.Add("--yolo");
        }
        else
        {
            if (rule.AllowAllTools) parts.Add("--allow-all-tools");
            if (rule.AllowAllUrls) parts.Add("--allow-all-urls");
        }

        if (rule.Model is not null)
            parts.Add($"--model={rule.Model}");

        return parts.Count > 0 ? string.Join(", ", parts) : "(defaults)";
    }

    private static string FormatAuthorMode(DispatchRule rule)
    {
        return rule.AuthorMode switch
        {
            AuthorMode.Allowed => string.Join(", ", rule.Authors),
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

        command.Arguments.Add(nameArg);
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

                if (config.Rules.ContainsKey(name))
                {
                    ConsoleOutput.Error($"Rule '{name}' already exists. Use 'rules update' to modify it.");
                    return 1;
                }

                var rule = new DispatchRule
                {
                    User = parseResult.GetValue(assigneeOption),
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
                    rule.AuthorMode = AuthorMode.WriteAccess;
                }
                else if (rule.Authors.Count > 0)
                {
                    rule.AuthorMode = AuthorMode.Allowed;
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
                    rule.CustomPromptMode = mode;
                }

                config.Rules[name] = rule;
                CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                    copilotTrust,
                    repoResolver,
                    copilotCli,
                    config,
                    state,
                    rule.Repos);
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

                if (!config.Rules.TryGetValue(name, out var rule))
                {
                    ConsoleOutput.Error($"Rule '{name}' not found.");
                    return 1;
                }

                if (parseResult.GetResult(assigneeOption) is not null)
                    rule.User = parseResult.GetValue(assigneeOption);

                var addLabels = parseResult.GetValue(addLabelOption) ?? [];
                var deleteLabels = parseResult.GetValue(deleteLabelOption) ?? [];
                foreach (var label in deleteLabels)
                    rule.Labels.Remove(label);
                foreach (var label in addLabels)
                {
                    if (!rule.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                        rule.Labels.Add(label);
                }

                if (parseResult.GetResult(milestoneOption) is not null)
                    rule.Milestone = parseResult.GetValue(milestoneOption);

                if (parseResult.GetResult(typeOption) is not null)
                    rule.Type = parseResult.GetValue(typeOption);

                if (parseResult.GetResult(yoloOption) is not null)
                    rule.Yolo = parseResult.GetValue(yoloOption) ?? false;

                if (parseResult.GetResult(allowAllToolsOption) is not null)
                    rule.AllowAllTools = parseResult.GetValue(allowAllToolsOption) ?? true;

                if (parseResult.GetResult(allowAllUrlsOption) is not null)
                    rule.AllowAllUrls = parseResult.GetValue(allowAllUrlsOption) ?? false;

                if (parseResult.GetResult(promptOption) is not null)
                    rule.ExtraPrompt = parseResult.GetValue(promptOption);

                if (parseResult.GetResult(modelOption) is not null)
                {
                    var modelValue = parseResult.GetValue(modelOption);
                    rule.Model = string.IsNullOrWhiteSpace(modelValue) ? null : modelValue;
                }

                if (parseResult.GetResult(customPromptOption) is not null)
                    rule.CustomPrompt = parseResult.GetValue(customPromptOption);

                if (parseResult.GetResult(customPromptModeOption) is not null)
                {
                    var modeValue = parseResult.GetValue(customPromptModeOption);
                    if (!TryParsePromptMode(modeValue, out var mode))
                    {
                        ConsoleOutput.Error("Invalid --custom-prompt-mode. Use 'append' or 'override'.");
                        return 1;
                    }
                    rule.CustomPromptMode = mode;
                }

                var addRepos = parseResult.GetValue(addRepoOption) ?? [];
                var deleteRepos = parseResult.GetValue(deleteRepoOption) ?? [];
                foreach (var repo in deleteRepos)
                    rule.Repos.RemoveAll(r => string.Equals(r, repo, StringComparison.OrdinalIgnoreCase));
                foreach (var repo in addRepos)
                {
                    if (!rule.Repos.Contains(repo, StringComparer.OrdinalIgnoreCase))
                        rule.Repos.Add(repo);
                }

                // Author mode updates: --any-author and --write-only-authors change the mode;
                // --add-author/--delete-author modify the allowed list (and imply Allowed mode)
                if (parseResult.GetValue(anyAuthorOption))
                {
                    rule.AuthorMode = AuthorMode.Any;
                    rule.Authors.Clear();
                }
                else if (parseResult.GetValue(writeOnlyAuthorsOption))
                {
                    rule.AuthorMode = AuthorMode.WriteAccess;
                    rule.Authors.Clear();
                }

                var addAuthors = parseResult.GetValue(addAuthorOption) ?? [];
                var deleteAuthors = parseResult.GetValue(deleteAuthorOption) ?? [];
                foreach (var author in deleteAuthors)
                    rule.Authors.RemoveAll(a => string.Equals(a, author, StringComparison.OrdinalIgnoreCase));
                foreach (var author in addAuthors)
                {
                    if (!rule.Authors.Contains(author, StringComparer.OrdinalIgnoreCase))
                        rule.Authors.Add(author);
                }

                // If authors were added and mode is still Any, switch to Allowed
                if (rule.Authors.Count > 0 && rule.AuthorMode == AuthorMode.Any)
                {
                    rule.AuthorMode = AuthorMode.Allowed;
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

                if (!config.Rules.Remove(name))
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
}
