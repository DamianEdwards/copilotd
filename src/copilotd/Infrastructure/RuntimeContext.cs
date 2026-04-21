namespace Copilotd.Infrastructure;

/// <summary>
/// Detects how copilotd is being run so commands, helper processes, and automatic
/// update policy can adapt to installed binaries, source-tree runs, and local publishes.
/// </summary>
public sealed class RuntimeContext
{
    public const string DisableSelfUpdatesEnvVar = "COPILOTD_DISABLE_SELF_UPDATES";

    public RuntimeContext()
    {
        ProcessPath = Environment.ProcessPath;
        SourceRoot = FindSourceRoot();
        SourceProjectPath = SourceRoot is null ? null : Path.Combine(SourceRoot, "src", "copilotd");
    }

    public string? ProcessPath { get; }

    public string? SourceRoot { get; }

    public string? SourceProjectPath { get; }

    public bool IsDotnetHosted =>
        string.Equals(Path.GetFileName(ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    public bool IsSourceTreeRun => SourceProjectPath is not null;

    private bool UsesDotnetSourceCommand => IsSourceTreeRun && !string.IsNullOrEmpty(SourceProjectPath);

    public string GetCopilotdCallbackCommand()
    {
        if (UsesDotnetSourceCommand)
            return $"dotnet run --project \"{SourceProjectPath}\" --no-build --";

        return "copilotd";
    }

    public IEnumerable<string> GetExtraAllowedDirectories()
    {
        if (!string.IsNullOrEmpty(SourceRoot))
            yield return SourceRoot;
    }

    public IEnumerable<string> GetControlSessionAllowedShellCommands()
    {
        if (UsesDotnetSourceCommand)
            yield return "dotnet";
        else
            yield return "copilotd";

        yield return "gh";
        yield return "git";
    }

    public bool IsAutomaticSelfUpdateDisabled(bool disableRequested)
        => disableRequested || IsSelfUpdateDisabledByEnvironment() || IsSourceTreeRun;

    public string? GetAutomaticSelfUpdateDisableReason(bool disableRequested)
    {
        if (disableRequested)
            return "disabled by --disable-self-updates";

        if (IsSelfUpdateDisabledByEnvironment())
            return $"disabled by {DisableSelfUpdatesEnvVar}";

        if (IsSourceTreeRun)
            return "disabled for source/dev runs";

        return null;
    }

    public bool SupportsInPlaceSelfUpdate()
        => !IsDotnetHosted && !string.IsNullOrEmpty(ProcessPath);

    public string? GetUnsupportedSelfUpdateReason()
        => IsDotnetHosted
            ? "Self-update is not supported when running copilotd via dotnet run. Publish or install a copilotd binary to test updates."
            : null;

    public CommandInvocation? GetSelfInvocation(string arguments)
    {
        if (IsDotnetHosted && !string.IsNullOrEmpty(SourceProjectPath))
        {
            var prefix = $"run --project \"{SourceProjectPath}\" --";
            var resolvedArguments = string.IsNullOrWhiteSpace(arguments)
                ? prefix
                : $"{prefix} {arguments}";
            return new CommandInvocation(ProcessPath ?? "dotnet", resolvedArguments);
        }

        if (string.IsNullOrEmpty(ProcessPath))
            return null;

        return new CommandInvocation(ProcessPath, arguments);
    }

    private static bool IsSelfUpdateDisabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DisableSelfUpdatesEnvVar);
        return value is not null
            && (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindSourceRoot()
    {
        foreach (var candidate in GetSearchRoots())
        {
            for (var dir = candidate; dir is not null; dir = Directory.GetParent(dir)?.FullName)
            {
                if (LooksLikeSourceRoot(dir))
                    return dir;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;

        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            var processDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(processDir))
                yield return processDir;
        }
    }

    private static bool LooksLikeSourceRoot(string path)
    {
        var projectPath = Path.Combine(path, "src", "copilotd", "copilotd.csproj");
        if (!File.Exists(projectPath))
            return false;

        return File.Exists(Path.Combine(path, "copilotd.cmd"))
            || File.Exists(Path.Combine(path, "copilotd.sh"));
    }
}

public sealed record CommandInvocation(string FileName, string Arguments)
{
    public string GetCommandLine()
        => string.IsNullOrWhiteSpace(Arguments)
            ? $"\"{FileName}\""
            : $"\"{FileName}\" {Arguments}";
}
