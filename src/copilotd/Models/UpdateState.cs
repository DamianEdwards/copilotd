namespace Copilotd.Models;

/// <summary>
/// Status of the self-update lifecycle.
/// </summary>
public enum UpdateStatus
{
    /// <summary>No update activity.</summary>
    None,

    /// <summary>Currently checking for a newer version.</summary>
    Checking,

    /// <summary>A newer version is available but not yet downloaded.</summary>
    Available,

    /// <summary>Downloading the update artifact.</summary>
    Downloading,

    /// <summary>Update binary is downloaded, verified, and staged for install.</summary>
    Staged,

    /// <summary>Install of the staged binary is in progress.</summary>
    Installing,

    /// <summary>The last update attempt failed.</summary>
    Failed
}

/// <summary>
/// Persisted update state stored at ~/.copilotd/update-state.json.
/// Tracks the current self-update lifecycle so the daemon and update command
/// can coordinate across process boundaries.
/// </summary>
public sealed class UpdateState
{
    public UpdateStatus Status { get; set; } = UpdateStatus.None;

    /// <summary>Version string of the available or staged update.</summary>
    public string? AvailableVersion { get; set; }

    /// <summary>Version string of the staged binary ready for install.</summary>
    public string? StagedVersion { get; set; }

    /// <summary>Full path to the staged binary (e.g. copilotd.exe.staged).</summary>
    public string? StagedPath { get; set; }

    /// <summary>GitHub release tag of the release being processed.</summary>
    public string? CurrentReleaseTag { get; set; }

    /// <summary>When the last update check was performed.</summary>
    public DateTimeOffset? LastCheckTime { get; set; }

    /// <summary>When the last install attempt started.</summary>
    public DateTimeOffset? LastAttemptTime { get; set; }

    /// <summary>Error message from the last failed attempt.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Consecutive failure count, used for exponential backoff.</summary>
    public int FailureCount { get; set; }
}
