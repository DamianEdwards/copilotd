using Copilotd.Infrastructure;
using Copilotd.Services;

namespace Copilotd.Commands;

/// <summary>
/// Shared pre-flight validation used by both the run and start commands.
/// </summary>
internal static class PreflightChecks
{
    /// <summary>
    /// Verifies that gh CLI, copilot CLI, and authentication are available, and config exists.
    /// Returns 0 on success, or a non-zero exit code with error messages written to console.
    /// </summary>
    public static int Run(GhCliService ghCli, CopilotCliService copilotCli, StateStore stateStore)
    {
        if (!ghCli.IsAvailable())
        {
            ConsoleOutput.Error("gh CLI is not available. Install from: https://cli.github.com/");
            return 1;
        }

        if (!copilotCli.IsAvailable())
        {
            ConsoleOutput.Error("copilot CLI is not available. Install from: https://docs.github.com/copilot/how-tos/copilot-cli");
            return 1;
        }

        var authResult = ghCli.CheckAuth();
        if (!authResult.IsLoggedIn)
        {
            ConsoleOutput.Error("gh CLI is not authenticated. Run 'gh auth login' first.");
            return 1;
        }

        if (!stateStore.ConfigExists())
        {
            ConsoleOutput.Error("copilotd is not configured. Run 'copilotd init' first.");
            return 1;
        }

        return 0;
    }
}
