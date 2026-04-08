using System.CommandLine;
using Copilotd.Infrastructure;
using Copilotd.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Copilotd.Commands;

public static class UpdateCommand
{
    /// <summary>
    /// Environment variable to override the update source with a local path
    /// for testing the update flow without a real release.
    /// Set to a directory containing the release assets (ZIP, checksums.txt, release-metadata.json).
    /// </summary>
    public const string UpdateSourceEnvVar = "COPILOTD_UPDATE_SOURCE";

    public static Command Create(IServiceProvider services)
    {
        var command = new Command("update", "Check for and install updates (Windows only)");

        var checkOption = new Option<bool>("--check")
        {
            Description = "Only check for updates without downloading or installing"
        };
        var preReleaseOption = new Option<bool>("--pre-release", "-p")
        {
            Description = "Include pre-release versions as update candidates"
        };
        var skipProvenanceOption = new Option<bool>("--skip-provenance-checks")
        {
            Description = "Disable Authenticode signature verification"
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would happen without making any changes"
        };
        var installStagedOption = new Option<bool>("--install-staged")
        {
            Description = "Install a previously staged update binary",
            Hidden = true
        };

        command.Options.Add(checkOption);
        command.Options.Add(preReleaseOption);
        command.Options.Add(skipProvenanceOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(installStagedOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    ConsoleOutput.Warning("Self-updating is currently only supported on Windows.");
                    return 1;
                }

                var updateService = services.GetRequiredService<UpdateService>();
                var stateStore = services.GetRequiredService<StateStore>();

                var check = parseResult.GetValue(checkOption);
                var preRelease = parseResult.GetValue(preReleaseOption);
                var skipProvenance = parseResult.GetValue(skipProvenanceOption);
                var dryRun = parseResult.GetValue(dryRunOption);
                var installStaged = parseResult.GetValue(installStagedOption);

                var localSource = Environment.GetEnvironmentVariable(UpdateSourceEnvVar);
                if (!string.IsNullOrEmpty(localSource))
                {
                    if (!Directory.Exists(localSource))
                    {
                        ConsoleOutput.Error($"{UpdateSourceEnvVar} directory not found: {localSource}");
                        return 1;
                    }
                    ConsoleOutput.Info($"Using local update source: {localSource}");
                }

                if (installStaged)
                {
                    if (dryRun)
                    {
                        ConsoleOutput.Info("[dry-run] Would install staged update.");
                        return 0;
                    }
                    return await HandleInstallStaged(updateService, stateStore, skipProvenance, ct);
                }

                if (check)
                {
                    return HandleCheckOnly(updateService, preRelease);
                }

                return await HandleFullUpdate(updateService, stateStore, preRelease, skipProvenance, dryRun, ct);
            }, logger);
        });

        return command;
    }

    private static async Task<int> HandleInstallStaged(UpdateService updateService, StateStore stateStore, bool skipProvenance, CancellationToken ct)
    {
        ConsoleOutput.Info("Installing staged update...");
        var success = await updateService.InstallStagedAsync(skipProvenance, ct);
        if (success)
        {
            ConsoleOutput.Success("Update installed successfully.");
            return 0;
        }

        var state = stateStore.LoadUpdateState();
        ConsoleOutput.Error($"Update installation failed: {state.ErrorMessage ?? "unknown error"}");
        ConsoleOutput.Info("Check the log file for details.");
        return 1;
    }

    private static int HandleCheckOnly(UpdateService updateService, bool preRelease)
    {
        ConsoleOutput.Info("Checking for updates...");
        var update = updateService.CheckForUpdate(preRelease);
        if (update is null)
        {
            ConsoleOutput.Success("copilotd is up to date.");
            return 0;
        }

        ConsoleOutput.Success($"Update available: {update.CurrentVersion} → {update.AvailableVersion}");
        ConsoleOutput.Info("Run 'copilotd update' to install.");
        return 0;
    }

    private static async Task<int> HandleFullUpdate(UpdateService updateService, StateStore stateStore, bool preRelease, bool skipProvenance, bool dryRun, CancellationToken ct)
    {
        // Check
        ConsoleOutput.Info("Checking for updates...");
        var update = updateService.CheckForUpdate(preRelease);
        if (update is null)
        {
            ConsoleOutput.Success("copilotd is up to date.");
            return 0;
        }

        ConsoleOutput.Info($"Update available: {update.CurrentVersion} → {update.AvailableVersion}");

        if (dryRun)
        {
            ConsoleOutput.Info("[dry-run] Would download, verify, and install this update.");
            ConsoleOutput.Info($"[dry-run] Release tag: {update.ReleaseTag}, Dev build: {update.IsDevBuild}");
            ConsoleOutput.Info($"[dry-run] Authenticode verification: {(update.IsDevBuild || skipProvenance ? "skipped" : "enabled")}");
            ConsoleOutput.Info($"[dry-run] Checksum verification: enabled");
            return 0;
        }

        // Acquire update lock for download/stage phase
        if (!stateStore.TryAcquireUpdateLock())
        {
            ConsoleOutput.Error("Another update operation is already in progress.");
            return 1;
        }

        // Download, verify, and stage
        ConsoleOutput.Info("Downloading and verifying update...");
        bool staged;
        try
        {
            staged = await updateService.DownloadAndStageAsync(update, skipProvenance, ct);
        }
        finally
        {
            // Release lock before install (InstallStagedAsync acquires its own lock)
            stateStore.ReleaseUpdateLock();
        }

        if (!staged)
        {
            var state = stateStore.LoadUpdateState();
            ConsoleOutput.Error($"Download/verification failed: {state.ErrorMessage ?? "unknown error"}");
            ConsoleOutput.Info("Check the log file for details.");
            return 1;
        }

        // Install directly (for manual invocation, we do it in-process)
        // InstallStagedAsync acquires and releases its own update lock
        ConsoleOutput.Info("Installing update...");
        var installed = await updateService.InstallStagedAsync(skipProvenance, ct);
        if (!installed)
        {
            var state = stateStore.LoadUpdateState();
            ConsoleOutput.Error($"Installation failed: {state.ErrorMessage ?? "unknown error"}");
            ConsoleOutput.Info("Check the log file for details.");
            return 1;
        }

        ConsoleOutput.Success($"copilotd updated to {update.AvailableVersion}.");
        return 0;
    }
}
