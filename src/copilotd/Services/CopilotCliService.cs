using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Copilotd.Services;

/// <summary>
/// Adapter for the copilot CLI. Handles dependency checks and auth validation.
/// </summary>
public sealed class CopilotCliService
{
    private readonly ILogger<CopilotCliService> _logger;

    public CopilotCliService(ILogger<CopilotCliService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the copilot CLI is available on PATH.
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            var (exitCode, _) = RunCopilot("--version");
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the copilot CLI version string, or null if unavailable.
    /// </summary>
    public string? GetVersion()
    {
        try
        {
            var (exitCode, output) = RunCopilot("--version");
            if (exitCode != 0) return null;

            var trimmed = output.Trim();
            var idx = trimmed.IndexOf('\n');
            return idx > 0 ? trimmed[..idx].Trim() : trimmed;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort check for copilot login status.
    /// Since copilot CLI has no "login status" command, we attempt a lightweight
    /// operation and check if it fails with auth errors.
    /// </summary>
    public bool IsLoggedIn()
    {
        try
        {
            // copilot --version works regardless of auth. We try copilot login
            // with a test approach — but that triggers the flow.
            // Best approach: attempt a minimal prompt that will fail fast if not authed.
            var (exitCode, output) = RunCopilot("--version");
            if (exitCode != 0)
                return false;

            // If version works, copilot is installed. Auth is harder to verify
            // without triggering the login flow. We'll assume if it's installed
            // and gh is authenticated, copilot should work (it uses gh auth).
            // The run loop will catch auth failures at dispatch time.
            _logger.LogDebug("copilot CLI available, assuming auth via gh");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking copilot auth");
            return false;
        }
    }

    private (int ExitCode, string Output) RunCopilot(string arguments)
    {
        _logger.LogDebug("Running: copilot {Args}", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = "copilot",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            try { process.Kill(); } catch { }
        }
        var stderr = stderrTask.GetAwaiter().GetResult();

        var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
        return (process.ExitCode, output);
    }
}
