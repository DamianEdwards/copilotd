using System.CommandLine;
using System.CommandLine.Help;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class RulesCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("rules", "Manage dispatch rules");
        command.Subcommands.Add(CreateList(services));
        command.Subcommands.Add(CreateAdd(services));
        command.Subcommands.Add(CreateUpdate(services));
        command.Subcommands.Add(CreateDelete(services));

        // Default to list behavior when no subcommand is specified
        var repoOption = new Option<string?>("--repo") { Description = "Filter rules by repository" };
        var userOption = new Option<string?>("--user") { Description = "Filter rules by user condition", Arity = ArgumentArity.ZeroOrOne };
        command.Options.Add(repoOption);
        command.Options.Add(userOption);

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
                var userFilter = parseResult.GetValue(userOption);
                var userFlagPresent = parseResult.GetResult(userOption) is not null;

                return RenderRulesList(config, repoFilter, userFilter, userFlagPresent);
            }, logger);
        });

        return command;
    }

    private static Command CreateList(IServiceProvider services)
    {
        var command = new Command("list", "List dispatch rules");
        var repoOption = new Option<string?>("--repo") { Description = "Filter rules by repository" };
        var userOption = new Option<string?>("--user") { Description = "Filter rules by user condition", Arity = ArgumentArity.ZeroOrOne };
        command.Options.Add(repoOption);
        command.Options.Add(userOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var config = stateStore.LoadConfig();
                var repoFilter = parseResult.GetValue(repoOption);
                var userFilter = parseResult.GetValue(userOption);
                var userFlagPresent = parseResult.GetResult(userOption) is not null;

                return RenderRulesList(config, repoFilter, userFilter, userFlagPresent);
            }, logger);
        });

        return command;
    }

    private static int RenderRulesList(CopilotdConfig config, string? repoFilter, string? userFilter, bool userFlagPresent)
    {
        var rules = config.Rules.AsEnumerable();

        if (repoFilter is not null)
            rules = rules.Where(r => r.Value.Repos.Contains(repoFilter, StringComparer.OrdinalIgnoreCase));

        if (userFlagPresent)
        {
            if (userFilter is not null)
                rules = rules.Where(r => string.Equals(r.Value.User, userFilter, StringComparison.OrdinalIgnoreCase));
            else
                rules = rules.Where(r => r.Value.User is not null);
        }

        var table = new Table();
        table.AddColumn("Name");
        table.AddColumn("User");
        table.AddColumn("Labels");
        table.AddColumn("Milestone");
        table.AddColumn("Type");
        table.AddColumn("Repos");
        table.AddColumn("Yolo");
        table.AddColumn("Tools");
        table.AddColumn("URLs");

        foreach (var kvp in rules)
        {
            var name = kvp.Key;
            var rule = kvp.Value;
            table.AddRow(
                Markup.Escape(name),
                Markup.Escape(rule.User ?? "*"),
                Markup.Escape(string.Join(", ", rule.Labels)),
                Markup.Escape(rule.Milestone ?? "*"),
                Markup.Escape(rule.Type ?? "*"),
                Markup.Escape(string.Join(", ", rule.Repos)),
                rule.Yolo ? "yes" : "no",
                rule.Yolo || rule.AllowAllTools ? "yes" : "no",
                rule.Yolo || rule.AllowAllUrls ? "yes" : "no");
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static Command CreateAdd(IServiceProvider services)
    {
        var command = new Command("add", "Add a new dispatch rule");
        var nameArg = new Argument<string>("name");
        var userOption = new Option<string?>("--user") { Description = "User condition" };
        var labelOption = new Option<string[]>("--label") { Description = "Label condition (can be specified multiple times)", AllowMultipleArgumentsPerToken = true };
        var milestoneOption = new Option<string?>("--milestone") { Description = "Milestone condition" };
        var typeOption = new Option<string?>("--type") { Description = "Issue type condition" };
        var yoloOption = new Option<bool>("--yolo") { Description = "Pass --yolo to copilot (implies --allow-all-tools and --allow-all-urls)" };
        var allowAllToolsOption = new Option<bool?>("--allow-all-tools") { Description = "Pass --allow-all-tools to copilot (default: true)" };
        var allowAllUrlsOption = new Option<bool?>("--allow-all-urls") { Description = "Pass --allow-all-urls to copilot (default: false)" };
        var promptOption = new Option<string?>("--prompt") { Description = "Extra prompt for this rule" };
        var repoOption = new Option<string[]>("--repo") { Description = "Repository to add (can be specified multiple times)", AllowMultipleArgumentsPerToken = true };

        command.Arguments.Add(nameArg);
        command.Options.Add(userOption);
        command.Options.Add(labelOption);
        command.Options.Add(milestoneOption);
        command.Options.Add(typeOption);
        command.Options.Add(yoloOption);
        command.Options.Add(allowAllToolsOption);
        command.Options.Add(allowAllUrlsOption);
        command.Options.Add(promptOption);
        command.Options.Add(repoOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var config = stateStore.LoadConfig();

                var name = parseResult.GetValue(nameArg)!;

                if (config.Rules.ContainsKey(name))
                {
                    ConsoleOutput.Error($"Rule '{name}' already exists. Use 'rules update' to modify it.");
                    return 1;
                }

                var rule = new DispatchRule
                {
                    User = parseResult.GetValue(userOption),
                    Labels = [.. parseResult.GetValue(labelOption) ?? []],
                    Milestone = parseResult.GetValue(milestoneOption),
                    Type = parseResult.GetValue(typeOption),
                    Yolo = parseResult.GetValue(yoloOption),
                    AllowAllTools = parseResult.GetValue(allowAllToolsOption) ?? true,
                    AllowAllUrls = parseResult.GetValue(allowAllUrlsOption) ?? false,
                    ExtraPrompt = parseResult.GetValue(promptOption),
                    Repos = [.. parseResult.GetValue(repoOption) ?? []],
                };

                config.Rules[name] = rule;
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
        var nameArg = new Argument<string>("name");
        var userOption = new Option<string?>("--user") { Description = "Update user condition" };
        var addLabelOption = new Option<string[]>("--add-label") { Description = "Add a label condition", AllowMultipleArgumentsPerToken = true };
        var deleteLabelOption = new Option<string[]>("--delete-label") { Description = "Remove a label condition", AllowMultipleArgumentsPerToken = true };
        var milestoneOption = new Option<string?>("--milestone") { Description = "Update milestone condition" };
        var typeOption = new Option<string?>("--type") { Description = "Update type condition" };
        var yoloOption = new Option<bool?>("--yolo") { Description = "Update yolo setting" };
        var allowAllToolsOption = new Option<bool?>("--allow-all-tools") { Description = "Update allow-all-tools setting" };
        var allowAllUrlsOption = new Option<bool?>("--allow-all-urls") { Description = "Update allow-all-urls setting" };
        var promptOption = new Option<string?>("--prompt") { Description = "Update extra prompt" };
        var addRepoOption = new Option<string[]>("--add-repo") { Description = "Add a repository", AllowMultipleArgumentsPerToken = true };
        var deleteRepoOption = new Option<string[]>("--delete-repo") { Description = "Remove a repository", AllowMultipleArgumentsPerToken = true };

        command.Arguments.Add(nameArg);
        command.Options.Add(userOption);
        command.Options.Add(addLabelOption);
        command.Options.Add(deleteLabelOption);
        command.Options.Add(milestoneOption);
        command.Options.Add(typeOption);
        command.Options.Add(yoloOption);
        command.Options.Add(allowAllToolsOption);
        command.Options.Add(allowAllUrlsOption);
        command.Options.Add(promptOption);
        command.Options.Add(addRepoOption);
        command.Options.Add(deleteRepoOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var config = stateStore.LoadConfig();

                var name = parseResult.GetValue(nameArg)!;

                if (!config.Rules.TryGetValue(name, out var rule))
                {
                    ConsoleOutput.Error($"Rule '{name}' not found.");
                    return 1;
                }

                if (parseResult.GetResult(userOption) is not null)
                    rule.User = parseResult.GetValue(userOption);

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

                var addRepos = parseResult.GetValue(addRepoOption) ?? [];
                var deleteRepos = parseResult.GetValue(deleteRepoOption) ?? [];
                foreach (var repo in deleteRepos)
                    rule.Repos.RemoveAll(r => string.Equals(r, repo, StringComparison.OrdinalIgnoreCase));
                foreach (var repo in addRepos)
                {
                    if (!rule.Repos.Contains(repo, StringComparer.OrdinalIgnoreCase))
                        rule.Repos.Add(repo);
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
}
