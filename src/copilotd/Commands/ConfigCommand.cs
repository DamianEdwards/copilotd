using System.CommandLine;
using Copilotd.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class ConfigCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("config", "Manage copilotd configuration");

        var setOption = new Option<string?>("--set") { Description = "Set a config value in key=value format (e.g., repo_home=/path/to/repos)" };
        command.Options.Add(setOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var setValue = parseResult.GetValue(setOption);

                if (string.IsNullOrWhiteSpace(setValue))
                {
                    // Display current config as a table
                    var config = stateStore.LoadConfig();
                    var table = new Table();
                    table.Border(TableBorder.Rounded);
                    table.ShowRowSeparators = true;
                    table.AddColumn(new TableColumn("[bold]Key[/]").NoWrap());
                    table.AddColumn(new TableColumn("[bold]Value[/]"));

                    table.AddRow("repo_home", Markup.Escape(config.RepoHome ?? "(not set)"));
                    table.AddRow("custom_prompt", Markup.Escape(string.IsNullOrEmpty(config.Prompt) ? "(not set)" : config.Prompt));
                    table.AddRow("current_user", Markup.Escape(config.CurrentUser ?? "(not set)"));
                    table.AddRow("max_instances", Markup.Escape(config.MaxInstances.ToString()));
                    table.AddRow("rules", Markup.Escape($"{config.Rules.Count} rule(s)"));

                    if (config.Rules.Count > 0)
                    {
                        foreach (var (name, rule) in config.Rules)
                        {
                            var details = new List<string>();
                            if (rule.User is not null) details.Add($"user={rule.User}");
                            if (rule.Labels.Count > 0) details.Add($"labels={string.Join(",", rule.Labels)}");
                            if (rule.Milestone is not null) details.Add($"milestone={rule.Milestone}");
                            if (rule.Type is not null) details.Add($"type={rule.Type}");
                            if (rule.Repos.Count > 0) details.Add($"repos={string.Join(",", rule.Repos)}");
                            if (rule.Yolo) details.Add("yolo=true");
                            else
                            {
                                if (rule.AllowAllTools) details.Add("allow_all_tools=true");
                                if (rule.AllowAllUrls) details.Add("allow_all_urls=true");
                            }
                            if (rule.ExtraPrompt is not null) details.Add($"extra_prompt={rule.ExtraPrompt}");

                            table.AddRow(
                                Markup.Escape($"  rule[{name}]"),
                                Markup.Escape(details.Count > 0 ? string.Join(", ", details) : "(no conditions)"));
                        }
                    }

                    AnsiConsole.Write(table);
                    return 0;
                }

                var eqIdx = setValue.IndexOf('=');
                if (eqIdx <= 0)
                {
                    ConsoleOutput.Error("Invalid format. Use --set key=value");
                    return 1;
                }

                var key = setValue[..eqIdx].Trim().ToLowerInvariant();
                var value = setValue[(eqIdx + 1)..].Trim();
                var cfg = stateStore.LoadConfig();

                switch (key)
                {
                    case "repo_home":
                        if (value.StartsWith('~'))
                        {
                            value = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                value[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        }
                        cfg.RepoHome = Path.GetFullPath(value);
                        ConsoleOutput.Success($"repo_home set to: {cfg.RepoHome}");
                        break;

                    case "custom_prompt":
                    case "prompt": // backward compat
                        cfg.Prompt = value;
                        ConsoleOutput.Success("custom_prompt updated.");
                        break;

                    case "max_instances":
                        if (int.TryParse(value, out var maxInst) && maxInst > 0)
                        {
                            cfg.MaxInstances = maxInst;
                            ConsoleOutput.Success($"max_instances set to: {maxInst}");
                        }
                        else
                        {
                            ConsoleOutput.Error("max_instances must be a positive integer");
                            return 1;
                        }
                        break;

                    default:
                        ConsoleOutput.Error($"Unknown config key: {key}");
                        ConsoleOutput.Info("Valid keys: repo_home, custom_prompt, max_instances");
                        return 1;
                }

                stateStore.SaveConfig(cfg);
                return 0;
            }, logger);
        });

        return command;
    }
}
