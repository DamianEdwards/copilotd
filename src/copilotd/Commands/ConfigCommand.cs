using System.CommandLine;
using Copilotd.Infrastructure;
using Copilotd.Services;
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
                var copilotCli = services.GetRequiredService<CopilotCliService>();
                var copilotTrust = services.GetRequiredService<CopilotTrustService>();
                var repoResolver = services.GetRequiredService<RepoPathResolver>();
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
                    table.AddRow("default_model", Markup.Escape(config.DefaultModel ?? "(not set)"));
                    table.AddRow("custom_prompt", Markup.Escape(string.IsNullOrEmpty(config.Prompt) ? "(not set)" : config.Prompt));
                    table.AddRow("current_user", Markup.Escape(config.CurrentUser ?? "(not set)"));
                    table.AddRow("max_instances", Markup.Escape(config.MaxInstances.ToString()));
                    table.AddRow("session_shutdown_delay_seconds", Markup.Escape(config.SessionShutdownDelaySeconds.ToString()));
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
                            if (rule.Model is not null) details.Add($"model={rule.Model}");
                            if (rule.ExtraPrompt is not null) details.Add($"extra_prompt={rule.ExtraPrompt}");
                            if (rule.CustomPrompt is not null) details.Add($"custom_prompt={rule.CustomPrompt}");
                            if (rule.CustomPrompt is not null) details.Add($"custom_prompt_mode={rule.CustomPromptMode.ToString().ToLowerInvariant()}");

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
                            value = CopilotdPaths.ExpandUserProfile(value);
                        }
                        cfg.RepoHome = Path.GetFullPath(value);
                        CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                            copilotTrust,
                            repoResolver,
                            copilotCli,
                            cfg,
                            stateStore.LoadState(),
                            cfg.Rules.Values
                                .SelectMany(rule => rule.Repos)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList());
                        ConsoleOutput.Success($"repo_home set to: {cfg.RepoHome}");
                        break;

                    case "custom_prompt":
                    case "prompt": // backward compat
                        cfg.Prompt = value;
                        ConsoleOutput.Success("custom_prompt updated.");
                        break;

                    case "default_model":
                    case "model": // convenience alias
                        cfg.DefaultModel = string.IsNullOrWhiteSpace(value) ? null : value;
                        ConsoleOutput.Success(cfg.DefaultModel is not null
                            ? $"default_model set to: {cfg.DefaultModel}"
                            : "default_model cleared.");
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

                    case "session_shutdown_delay_seconds":
                    case "shutdown_delay_seconds":
                        if (int.TryParse(value, out var shutdownDelaySeconds) && shutdownDelaySeconds >= 0)
                        {
                            cfg.SessionShutdownDelaySeconds = shutdownDelaySeconds;
                            ConsoleOutput.Success($"session_shutdown_delay_seconds set to: {shutdownDelaySeconds}");
                        }
                        else
                        {
                            ConsoleOutput.Error("session_shutdown_delay_seconds must be a non-negative integer");
                            return 1;
                        }
                        break;

                    default:
                        ConsoleOutput.Error($"Unknown config key: {key}");
                        ConsoleOutput.Info("Valid keys: repo_home, default_model, custom_prompt, max_instances, session_shutdown_delay_seconds");
                        return 1;
                }

                stateStore.SaveConfig(cfg);
                return 0;
            }, logger);
        });

        return command;
    }
}
