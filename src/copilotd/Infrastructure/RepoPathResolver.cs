using System.Collections.Concurrent;
using System.Diagnostics;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Resolves the local filesystem path for a GitHub repository using a tiered strategy:
/// 1. Check in-memory + persisted cache (validated against remote URL)
/// 2. Direct check: &lt;RepoHome&gt;/&lt;repo_name&gt; (flat layout)
/// 3. Direct check: &lt;RepoHome&gt;/&lt;org&gt;/&lt;repo_name&gt; (nested layout)
/// 4. Scan RepoHome (up to 2 levels) for matching git remotes
/// </summary>
public sealed class RepoPathResolver
{
    private readonly ILogger<RepoPathResolver> _logger;
    private readonly ConcurrentDictionary<string, string> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    public RepoPathResolver(ILogger<RepoPathResolver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves the local path for a repo slug (e.g., "org/repo").
    /// Returns null if the repo cannot be found locally.
    /// </summary>
    public string? ResolveRepoPath(string repoSlug, CopilotdConfig config, DaemonState state)
    {
        var repoHome = config.RepoHome;
        if (string.IsNullOrEmpty(repoHome))
        {
            _logger.LogWarning("RepoHome is not configured");
            return null;
        }

        var parts = repoSlug.Split('/', 2);
        if (parts.Length != 2)
        {
            _logger.LogWarning("Invalid repo slug format: {Slug} (expected org/repo)", repoSlug);
            return null;
        }

        var org = parts[0];
        var repoName = parts[1];

        // 1. Check cache (memory first, then persisted state) — validate remote still matches
        if (_memoryCache.TryGetValue(repoSlug, out var cached) && IsValidRepoForSlug(cached, repoSlug, repoHome))
        {
            _logger.LogDebug("Resolved {Slug} from memory cache: {Path}", repoSlug, cached);
            return cached;
        }

        if (state.ResolvedRepoPaths.TryGetValue(repoSlug, out var persisted) && IsValidRepoForSlug(persisted, repoSlug, repoHome))
        {
            _memoryCache[repoSlug] = persisted;
            _logger.LogDebug("Resolved {Slug} from persisted cache: {Path}", repoSlug, persisted);
            return persisted;
        }

        // Invalidate stale cache entries
        _memoryCache.TryRemove(repoSlug, out _);
        state.ResolvedRepoPaths.Remove(repoSlug);

        // 2. Direct check: <RepoHome>/<repo_name> (flat layout)
        var flatPath = Path.GetFullPath(Path.Combine(repoHome, repoName));
        if (IsValidRepoForSlug(flatPath, repoSlug, repoHome))
        {
            CachePath(repoSlug, flatPath, state);
            _logger.LogInformation("Resolved {Slug} via flat layout: {Path}", repoSlug, flatPath);
            return flatPath;
        }

        // 3. Direct check: <RepoHome>/<org>/<repo_name> (nested layout)
        var nestedPath = Path.GetFullPath(Path.Combine(repoHome, org, repoName));
        if (IsValidRepoForSlug(nestedPath, repoSlug, repoHome))
        {
            CachePath(repoSlug, nestedPath, state);
            _logger.LogInformation("Resolved {Slug} via nested layout: {Path}", repoSlug, nestedPath);
            return nestedPath;
        }

        // 4. Scan RepoHome for matching git remotes (up to 2 levels deep)
        var scanResult = ScanForRepo(repoHome, repoSlug);
        if (scanResult is not null)
        {
            CachePath(repoSlug, scanResult, state);
            _logger.LogInformation("Resolved {Slug} via scan: {Path}", repoSlug, scanResult);
            return scanResult;
        }

        _logger.LogWarning("Could not find local clone for {Slug} under {RepoHome}. " +
            "Ensure the repository is cloned somewhere under the configured RepoHome directory.",
            repoSlug, repoHome);
        return null;
    }

    /// <summary>
    /// Invalidates the cached path for a repo slug (e.g., after detecting it's stale).
    /// </summary>
    public void InvalidateCache(string repoSlug, DaemonState state)
    {
        _memoryCache.TryRemove(repoSlug, out _);
        state.ResolvedRepoPaths.Remove(repoSlug);
    }

    /// <summary>
    /// Efficiently checks clone status for many repos at once by scanning RepoHome once
    /// and building a complete slug→path map, then looking up each repo.
    /// Much faster than calling ResolveRepoPath per-repo when many repos aren't cloned
    /// (avoids repeated full scans).
    /// </summary>
    public Dictionary<string, bool> BuildCloneStatusMap(IReadOnlyList<string> repoSlugs, CopilotdConfig config)
    {
        var repoHome = config.RepoHome;
        var result = new Dictionary<string, bool>(repoSlugs.Count, StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(repoHome))
        {
            foreach (var slug in repoSlugs)
                result[slug] = false;
            return result;
        }

        // Scan once to build a map of all local repos: slug → path
        var localRepos = ScanAllRepos(repoHome);

        foreach (var slug in repoSlugs)
            result[slug] = localRepos.ContainsKey(slug);

        return result;
    }

    /// <summary>
    /// Scans RepoHome once and returns the normalized slugs for all local git repositories found.
    /// </summary>
    public HashSet<string> ListClonedRepoSlugs(CopilotdConfig config)
    {
        var repoHome = config.RepoHome;
        if (string.IsNullOrEmpty(repoHome))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(ScanAllRepos(repoHome).Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans RepoHome up to 2 levels deep and resolves the origin remote for every git repo found.
    /// Returns a dictionary mapping normalized repo slugs to their local paths.
    /// </summary>
    private Dictionary<string, string> ScanAllRepos(string repoHome)
    {
        _logger.LogDebug("Scanning all repos under {RepoHome}", repoHome);

        var candidates = FindGitRepoDirs(repoHome);

        _logger.LogDebug("Found {Count} git repositories to index under {RepoHome}", candidates.Count, repoHome);

        // Resolve remotes in parallel
        var maxParallelism = Math.Max(Math.Min(Environment.ProcessorCount / 2, 8), 1);
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism }, candidate =>
        {
            try
            {
                var originUrl = GetGitRemoteUrl(candidate, "origin");
                if (originUrl is null) return;

                var slug = NormalizeRemoteUrl(originUrl);
                if (slug is not null)
                    result.TryAdd(slug, candidate);
            }
            catch
            {
                // Skip repos we can't read
            }
        });

        _logger.LogDebug("Indexed {Count} repos with valid origins under {RepoHome}", result.Count, repoHome);
        return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds all directories containing a .git folder/file up to 2 levels under repoHome.
    /// Excludes daemon-managed worktree directories.
    /// </summary>
    private List<string> FindGitRepoDirs(string repoHome)
    {
        var candidates = new List<string>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(repoHome))
            {
                var dirName = Path.GetFileName(dir);
                if (IsDaemonWorktreeDir(dirName))
                    continue;

                if (IsValidRepoDir(dir))
                    candidates.Add(dir);

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(dir))
                    {
                        var subDirName = Path.GetFileName(subDir);
                        if (IsDaemonWorktreeDir(subDirName))
                            continue;

                        if (IsValidRepoDir(subDir))
                            candidates.Add(subDir);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return candidates;
    }

    private void CachePath(string repoSlug, string path, DaemonState state)
    {
        _memoryCache[repoSlug] = path;
        state.ResolvedRepoPaths[repoSlug] = path;
    }

    /// <summary>
    /// Validates that a directory is a git repo whose remote matches the expected slug,
    /// and that the path is under the current RepoHome.
    /// </summary>
    private bool IsValidRepoForSlug(string path, string expectedSlug, string repoHome)
        => path.StartsWith(Path.GetFullPath(repoHome), StringComparison.OrdinalIgnoreCase)
           && IsValidRepoDir(path)
           && MatchesRemote(path, expectedSlug);

    /// <summary>
    /// Checks if a directory exists and contains a .git folder or file (worktrees use a .git file).
    /// </summary>
    private static bool IsValidRepoDir(string path)
        => Directory.Exists(path) && (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));

    /// <summary>
    /// Checks if a directory name indicates a daemon-managed worktree (e.g., repo_sessions/).
    /// These should be excluded from scan results.
    /// </summary>
    private static bool IsDaemonWorktreeDir(string dirName)
        => dirName.EndsWith("_sessions", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs 'git remote get-url origin' and checks if it matches the expected slug.
    /// Only checks origin (not upstream) because PrepareWorktree always fetches from origin
    /// and creates branches from origin's default branch.
    /// </summary>
    private bool MatchesRemote(string repoDir, string expectedSlug)
    {
        try
        {
            var originUrl = GetGitRemoteUrl(repoDir, "origin");
            if (originUrl is null) return false;

            var slug = NormalizeRemoteUrl(originUrl);
            return string.Equals(slug, expectedSlug, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check remote for {Path}", repoDir);
            return false;
        }
    }

    /// <summary>
    /// Scans RepoHome up to 2 levels deep for a matching git remote.
    /// Excludes daemon-managed worktree directories.
    /// </summary>
    private string? ScanForRepo(string repoHome, string expectedSlug)
    {
        _logger.LogDebug("Scanning {RepoHome} for clone of {Slug}", repoHome, expectedSlug);

        var candidates = FindGitRepoDirs(repoHome);

        _logger.LogDebug("Found {Count} git repositories to check under {RepoHome}", candidates.Count, repoHome);

        // Check candidates in parallel — each check shells out to git which is ~50-100ms,
        // so parallelism helps when RepoHome contains many repositories
        var maxParallelism = Math.Min(Environment.ProcessorCount / 2, 8);
        maxParallelism = Math.Max(maxParallelism, 1);
        string? match = null;
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism }, (candidate, loopState) =>
        {
            if (MatchesRemote(candidate, expectedSlug))
            {
                Interlocked.CompareExchange(ref match, candidate, null);
                loopState.Stop();
            }
        });

        return match;
    }

    /// <summary>
    /// Runs 'git remote get-url' for the specified remote name in the given directory.
    /// </summary>
    private static string? GetGitRemoteUrl(string repoDir, string remoteName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"remote get-url {remoteName}",
                WorkingDirectory = repoDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            return process.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a git remote URL to an "org/repo" slug.
    /// Handles HTTPS, SSH, and various trailing formats.
    /// </summary>
    internal static string? NormalizeRemoteUrl(string url)
    {
        // Strip trailing .git
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // Strip trailing slash
        url = url.TrimEnd('/');

        // SSH format: git@github.com:org/repo
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colonIndex = url.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < url.Length - 1)
            {
                return url[(colonIndex + 1)..];
            }
            return null;
        }

        // HTTPS format: https://github.com/org/repo
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.TrimStart('/').TrimEnd('/');
            return string.IsNullOrEmpty(path) ? null : path;
        }

        // ssh://git@github.com/org/repo
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.TrimStart('/').TrimEnd('/');
            return string.IsNullOrEmpty(path) ? null : path;
        }

        return null;
    }
}
