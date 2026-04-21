using Copilotd.Infrastructure;
using Copilotd.Services;
using Spectre.Console;

namespace Copilotd.Commands;

internal static class CopilotTrustCommandHelper
{
    public static void EnsureTrustedFoldersForRepositories(
        CopilotTrustService trustService,
        RepoPathResolver repoResolver,
        CopilotCliService copilotCli,
        Copilotd.Models.CopilotdConfig config,
        Copilotd.Models.DaemonState state,
        IEnumerable<string> repoSlugs)
    {
        var requiredFolders = repoSlugs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(repoSlug => repoResolver.ResolveRepoPath(repoSlug, config, state))
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .SelectMany(path => trustService.GetRequiredTrustedFolders(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requiredFolders.Count == 0)
            return;

        var trustCheck = trustService.CheckTrustedFolders(requiredFolders);
        switch (trustCheck.Status)
        {
            case CopilotTrustStatus.Trusted:
                return;

            case CopilotTrustStatus.Unknown:
                RenderAutomaticTrustUnavailable(trustService, copilotCli, trustCheck);
                return;

            case CopilotTrustStatus.Untrusted:
                RenderMissingTrustPrompt(trustService, copilotCli, trustCheck);
                return;
        }
    }

    private static void RenderMissingTrustPrompt(
        CopilotTrustService trustService,
        CopilotCliService copilotCli,
        CopilotTrustCheckResult trustCheck)
    {
        ConsoleOutput.Warning("Copilot needs these folders trusted before copilotd can dispatch sessions reliably:");
        RenderFolderList(trustCheck.MissingFolders);

        if (!AnsiConsole.Confirm("Add the missing folders to Copilot trustedFolders now?", true))
        {
            ConsoleOutput.Warning("copilotd will keep monitoring these repositories, but dispatched sessions may fail until these folders are trusted.");
            RenderManualTrustInstructions(trustCheck.MissingFolders, copilotCli, includeIssueReportGuidance: false);
            return;
        }

        var updateResult = trustService.AddTrustedFolders(trustCheck.MissingFolders);
        if (updateResult.Succeeded)
        {
            if (updateResult.AddedFolders.Count > 0)
                ConsoleOutput.Success("Updated Copilot trusted folders for unattended dispatch.");
            return;
        }

        ConsoleOutput.Warning(updateResult.Message ?? "Copilot trusted folders could not be updated automatically.");
        RenderAutomaticTrustUnavailable(trustService, copilotCli, new CopilotTrustCheckResult
        {
            Status = CopilotTrustStatus.Unknown,
            RequiredFolders = trustCheck.RequiredFolders,
            Message = updateResult.Message,
        });
    }

    private static void RenderAutomaticTrustUnavailable(
        CopilotTrustService trustService,
        CopilotCliService copilotCli,
        CopilotTrustCheckResult trustCheck)
    {
        ConsoleOutput.Warning(trustCheck.Message ?? "Copilot folder trust could not be verified automatically.");
        ConsoleOutput.Info($"copilotd could not safely verify or update {trustService.ConfigPath}.");
        RenderManualTrustInstructions(trustCheck.RequiredFolders, copilotCli, includeIssueReportGuidance: true);
    }

    private static void RenderManualTrustInstructions(
        IReadOnlyList<string> folders,
        CopilotCliService copilotCli,
        bool includeIssueReportGuidance)
    {
        ConsoleOutput.Info("Trust these folders manually by starting a Copilot session in each folder and choosing the option to trust it for all sessions:");
        RenderFolderList(folders);

        if (includeIssueReportGuidance)
        {
            var version = copilotCli.GetVersion() ?? "unknown";
            ConsoleOutput.Info($"Please also file an issue on the copilotd repo and include your Copilot CLI version: {version}");
        }
    }

    private static void RenderFolderList(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
            AnsiConsole.MarkupLine($"  [yellow]- {Markup.Escape(folder)}[/]");
    }
}
