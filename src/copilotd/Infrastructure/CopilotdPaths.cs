namespace Copilotd.Infrastructure;

public static class CopilotdPaths
{
    public const string HomeEnvVar = "COPILOTD_HOME";

    public static string GetUserProfileDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Gets the root directory copilotd uses for its own persisted files.
    /// Defaults to ~/.copilotd but can be overridden by COPILOTD_HOME.
    /// </summary>
    public static string GetCopilotdHomeDirectory()
    {
        var configuredHome = Environment.GetEnvironmentVariable(HomeEnvVar);
        if (!string.IsNullOrWhiteSpace(configuredHome))
            return Path.GetFullPath(ExpandUserProfile(configuredHome));

        return Path.Combine(GetUserProfileDirectory(), ".copilotd");
    }

    public static string ExpandUserProfile(string path)
    {
        if (!path.StartsWith('~'))
            return path;

        return Path.Combine(
            GetUserProfileDirectory(),
            path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public static string GetConfigDirDescription()
        => Environment.GetEnvironmentVariable(HomeEnvVar) is { Length: > 0 }
            ? $"{GetCopilotdHomeDirectory()} (from {HomeEnvVar})"
            : GetCopilotdHomeDirectory();

    public static string GetCopilotHomeDirectory()
        => Path.Combine(GetUserProfileDirectory(), ".copilot");

    public static string GetCopilotConfigPath()
        => Path.Combine(GetCopilotHomeDirectory(), "config.json");

    public static string GetLogsDirectory()
        => Path.Combine(GetCopilotdHomeDirectory(), "logs");
}
