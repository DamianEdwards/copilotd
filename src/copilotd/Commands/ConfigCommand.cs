using System.CommandLine;
using Copilotd.Infrastructure;
using Copilotd.Models;
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
                    table.AddRow("session_name_format", Markup.Escape(string.IsNullOrWhiteSpace(config.SessionNameFormat) ? "(disabled)" : config.SessionNameFormat));
                    table.AddRow("current_user", Markup.Escape(config.CurrentUser ?? "(not set)"));
                    table.AddRow("max_instances", Markup.Escape(config.MaxInstances.ToString()));
                    table.AddRow("session_shutdown_delay_seconds", Markup.Escape(config.SessionShutdownDelaySeconds.ToString()));
                    table.AddRow("issue_rules", Markup.Escape($"{config.IssueRules.Count} rule(s)"));
                    table.AddRow("pull_request_rules", Markup.Escape($"{config.PullRequestRules.Count} rule(s)"));

                    if (config.IssueRules.Count > 0)
                    {
                        foreach (var (name, issueRule) in config.IssueRules)
                        {
                            var details = new List<string>();
                            if (issueRule.Assignee is not null) details.Add($"assignee={issueRule.Assignee}");
                            if (issueRule.Labels.Count > 0) details.Add($"labels={string.Join(",", issueRule.Labels)}");
                            if (issueRule.Milestone is not null) details.Add($"milestone={issueRule.Milestone}");
                            if (issueRule.Type is not null) details.Add($"type={issueRule.Type}");
                            if (issueRule.Repos.Count > 0) details.Add($"repos={string.Join(",", issueRule.Repos)}");
                            if (issueRule.Yolo) details.Add("yolo=true");
                            else
                            {
                                if (issueRule.AllowAllTools) details.Add("allow_all_tools=true");
                                if (issueRule.AllowAllUrls) details.Add("allow_all_urls=true");
                            }
                            if (issueRule.Model is not null) details.Add($"model={issueRule.Model}");
                            if (issueRule.ExtraPrompt is not null) details.Add($"extra_prompt={issueRule.ExtraPrompt}");
                            if (issueRule.CustomPrompt is not null) details.Add($"custom_prompt={issueRule.CustomPrompt}");
                            if (issueRule.CustomPrompt is not null) details.Add($"custom_prompt_mode={issueRule.CustomPromptMode.ToString().ToLowerInvariant()}");

                            table.AddRow(
                                Markup.Escape($"  issue_rule[{name}]"),
                                Markup.Escape(details.Count > 0 ? string.Join(", ", details) : "(no conditions)"));
                        }
                    }

                    if (config.PullRequestRules.Count > 0)
                    {
                        foreach (var (name, pullRequestRule) in config.PullRequestRules)
                        {
                            var details = new List<string>();
                            if (pullRequestRule.Assignee is not null) details.Add($"assignee={pullRequestRule.Assignee}");
                            if (pullRequestRule.Labels.Count > 0) details.Add($"labels={string.Join(",", pullRequestRule.Labels)}");
                            if (pullRequestRule.BaseBranch is not null) details.Add($"base={pullRequestRule.BaseBranch}");
                            if (pullRequestRule.HeadBranch is not null) details.Add($"head={pullRequestRule.HeadBranch}");
                            if (pullRequestRule.HeadRepo is not null) details.Add($"head_repo={pullRequestRule.HeadRepo}");
                            if (pullRequestRule.Draft is not null) details.Add($"draft={pullRequestRule.Draft.Value.ToString().ToLowerInvariant()}");
                            if (pullRequestRule.ReviewDecision is not null) details.Add($"review_decision={pullRequestRule.ReviewDecision}");
                            details.Add($"branch_strategy={pullRequestRule.BranchStrategy.ToString().ToLowerInvariant()}");
                            if (pullRequestRule.Repos.Count > 0) details.Add($"repos={string.Join(",", pullRequestRule.Repos)}");
                            if (pullRequestRule.Yolo) details.Add("yolo=true");
                            else
                            {
                                if (pullRequestRule.AllowAllTools) details.Add("allow_all_tools=true");
                                if (pullRequestRule.AllowAllUrls) details.Add("allow_all_urls=true");
                            }
                            if (pullRequestRule.Model is not null) details.Add($"model={pullRequestRule.Model}");
                            if (pullRequestRule.ExtraPrompt is not null) details.Add($"extra_prompt={pullRequestRule.ExtraPrompt}");
                            if (pullRequestRule.CustomPrompt is not null) details.Add($"custom_prompt={pullRequestRule.CustomPrompt}");
                            if (pullRequestRule.CustomPrompt is not null) details.Add($"custom_prompt_mode={pullRequestRule.CustomPromptMode.ToString().ToLowerInvariant()}");

                            table.AddRow(
                                Markup.Escape($"  pr_rule[{name}]"),
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
                                cfg.IssueRules.Values.Cast<DispatchRuleOptions>()
                                    .Concat(cfg.PullRequestRules.Values)
                                    .SelectMany(dispatchRule => dispatchRule.Repos)
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

                    case "session_name_format":
                        cfg.SessionNameFormat = value;
                        ConsoleOutput.Success(string.IsNullOrWhiteSpace(cfg.SessionNameFormat)
                            ? "session_name_format cleared; session naming disabled."
                            : $"session_name_format set to: {cfg.SessionNameFormat}");
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
                        ConsoleOutput.Info("Valid keys: repo_home, default_model, custom_prompt, session_name_format, max_instances, session_shutdown_delay_seconds");
                        return 1;
                }

                stateStore.SaveConfig(cfg);
                return 0;
            }, logger);
        });

        return command;
    }
}
