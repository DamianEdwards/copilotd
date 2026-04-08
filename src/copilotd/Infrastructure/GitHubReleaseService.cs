using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Result of querying a GitHub release.
/// </summary>
public sealed record ReleaseInfo(
    string TagName,
    string Name,
    bool IsPrerelease,
    bool IsDraft);

/// <summary>
/// Queries GitHub Releases and downloads assets using the gh CLI.
/// Mirrors the release selection logic from install-copilotd.ps1.
/// When <c>COPILOTD_UPDATE_SOURCE</c> is set, reads assets from a local directory instead.
/// </summary>
public sealed class GitHubReleaseService
{
    private readonly ILogger<GitHubReleaseService> _logger;
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GhDownloadTimeout = TimeSpan.FromMinutes(5);

    public const string Repository = "DamianEdwards/copilotd";

    /// <summary>
    /// Optional local directory path to use instead of GitHub Releases.
    /// Set via the <c>COPILOTD_UPDATE_SOURCE</c> environment variable.
    /// The directory should contain the release ZIP, checksums.txt, and release-metadata.json.
    /// </summary>
    public string? LocalSource { get; set; }

    public GitHubReleaseService(ILogger<GitHubReleaseService> logger)
    {
        _logger = logger;
        LocalSource = Environment.GetEnvironmentVariable("COPILOTD_UPDATE_SOURCE");
    }

    /// <summary>
    /// Gets the latest release matching the requested quality level that contains the specified asset.
    /// Mirrors <c>Get-ReleaseForQuality</c> from install-copilotd.ps1.
    /// </summary>
    /// <param name="quality">"Dev", "PreRelease", or "Stable".</param>
    /// <param name="assetName">Required asset name (e.g. "copilotd-win-x64.zip").</param>
    public ReleaseInfo? GetLatestRelease(string quality, string assetName)
    {
        // When using a local source, build ReleaseInfo from release-metadata.json
        if (!string.IsNullOrEmpty(LocalSource))
            return GetLocalRelease(assetName);

        if (string.Equals(quality, "Dev", StringComparison.OrdinalIgnoreCase))
        {
            return GetDevRelease(assetName);
        }

        // Fetch all releases and find the first matching one
        var (exitCode, output) = RunGh($"api repos/{Repository}/releases?per_page=100");
        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to list releases: {Output}", output);
            return null;
        }

        using var doc = JsonDocument.Parse(output);
        ReleaseInfo? fallbackPreRelease = null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var isDraft = release.TryGetProperty("draft", out var d) && d.GetBoolean();
            if (isDraft) continue;

            var tagName = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (tagName is "dev" or "install-scripts") continue;

            if (!ReleaseHasAsset(release, assetName)) continue;

            var isPrerelease = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            var name = release.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var info = new ReleaseInfo(tagName, name, isPrerelease, isDraft);

            if (string.Equals(quality, "Stable", StringComparison.OrdinalIgnoreCase) && isPrerelease)
            {
                fallbackPreRelease ??= info;
                continue;
            }

            return info;
        }

        // When looking for Stable and none found, fall back to latest prerelease
        if (string.Equals(quality, "Stable", StringComparison.OrdinalIgnoreCase) && fallbackPreRelease is not null)
        {
            _logger.LogWarning("No stable release found, falling back to latest prerelease");
            return fallbackPreRelease;
        }

        _logger.LogWarning("No {Quality} release containing '{AssetName}' was found", quality, assetName);
        return null;
    }

    private ReleaseInfo? GetDevRelease(string assetName)
    {
        // Try the "dev" tag first
        var (exitCode, output) = RunGh($"api repos/{Repository}/releases/tags/dev");
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
        {
            using var doc = JsonDocument.Parse(output);
            if (ReleaseHasAsset(doc.RootElement, assetName))
            {
                var tagName = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "dev" : "dev";
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                return new ReleaseInfo(tagName, name, true, false);
            }
        }

        // Fall back to searching all releases for "Development Build"
        (exitCode, output) = RunGh($"api repos/{Repository}/releases?per_page=100");
        if (exitCode != 0) return null;

        using var allDoc = JsonDocument.Parse(output);
        foreach (var release in allDoc.RootElement.EnumerateArray())
        {
            var name = release.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!string.Equals(name, "Development Build", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ReleaseHasAsset(release, assetName)) continue;

            var tagName = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            return new ReleaseInfo(tagName, name, true, false);
        }

        _logger.LogWarning("Could not locate development build release");
        return null;
    }

    /// <summary>
    /// Builds a <see cref="ReleaseInfo"/> from a local directory's release-metadata.json.
    /// </summary>
    private ReleaseInfo? GetLocalRelease(string assetName)
    {
        var assetPath = Path.Combine(LocalSource!, assetName);
        if (!File.Exists(assetPath))
        {
            _logger.LogWarning("Local source '{Path}' does not contain '{Asset}'", LocalSource, assetName);
            return null;
        }

        var metadataPath = Path.Combine(LocalSource!, "release-metadata.json");
        if (!File.Exists(metadataPath))
        {
            _logger.LogWarning("Local source '{Path}' does not contain release-metadata.json", LocalSource);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "local" : "local";
            var isPrerelease = doc.RootElement.TryGetProperty("prerelease", out var p) && p.GetBoolean();

            _logger.LogInformation("Using local release: version={Version}, asset={Asset}", version, assetName);
            return new ReleaseInfo(version, $"Local build ({version})", isPrerelease, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local release-metadata.json");
            return null;
        }
    }

    /// <summary>
    /// Fetches the version string from the release-metadata.json asset of a release.
    /// Used for dev builds where the tag ("dev") doesn't carry a parseable version.
    /// </summary>
    public string? GetDevReleaseVersion(string tag)
    {
        // When using a local source, read directly from local release-metadata.json
        if (!string.IsNullOrEmpty(LocalSource))
        {
            var localMeta = Path.Combine(LocalSource, "release-metadata.json");
            if (!File.Exists(localMeta)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(localMeta));
                return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            }
            catch { return null; }
        }

        _logger.LogDebug("Fetching release-metadata.json for version from release '{Tag}'", tag);

        var tempDir = Path.Combine(Path.GetTempPath(), $"copilotd-meta-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            if (!DownloadReleaseAsset(tag, "release-metadata.json", tempDir))
                return null;

            var metadataPath = Path.Combine(tempDir, "release-metadata.json");
            var json = File.ReadAllText(metadataPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("version", out var versionEl) && versionEl.ValueKind == JsonValueKind.String)
                return versionEl.GetString();

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read version from release-metadata.json");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Downloads a release asset using the gh CLI.
    /// </summary>
    public bool DownloadReleaseAsset(string tag, string assetName, string destinationDir)
    {
        // When using a local source, copy from the local directory
        if (!string.IsNullOrEmpty(LocalSource))
        {
            var localPath = Path.Combine(LocalSource, assetName);
            if (!File.Exists(localPath))
            {
                _logger.LogWarning("Local asset '{AssetName}' not found at '{Path}'", assetName, localPath);
                return false;
            }

            Directory.CreateDirectory(destinationDir);
            File.Copy(localPath, Path.Combine(destinationDir, assetName), overwrite: true);
            _logger.LogDebug("Copied local asset '{AssetName}' from '{Source}'", assetName, localPath);
            return true;
        }

        _logger.LogDebug("Downloading asset '{AssetName}' from release '{Tag}'", assetName, tag);

        Directory.CreateDirectory(destinationDir);
        var (exitCode, output) = RunGh(
            $"release download {tag} -R {Repository} -p \"{assetName}\" -D \"{destinationDir}\" --clobber",
            GhDownloadTimeout);

        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to download '{AssetName}': {Output}", assetName, output);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts a ZIP archive to a destination directory.
    /// </summary>
    public static string ExtractReleaseArchive(string archivePath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);

        var binaryPath = Path.Combine(destinationDir, "copilotd.exe");
        if (!File.Exists(binaryPath))
            throw new FileNotFoundException($"Archive did not contain copilotd.exe", binaryPath);

        return binaryPath;
    }

    /// <summary>
    /// Gets the architecture-specific asset name for the current platform.
    /// </summary>
    public static string GetWindowsAssetName()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var archStr = arch switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {arch}")
        };
        return $"copilotd-win-{archStr}.zip";
    }

    private static bool ReleaseHasAsset(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets))
            return false;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private (int ExitCode, string Output) RunGh(string arguments, TimeSpan? timeout = null)
    {
        _logger.LogDebug("Running: gh {Args}", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        var effectiveTimeout = timeout ?? GhTimeout;
        if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
        {
            _logger.LogWarning("gh command timed out after {Timeout}s: gh {Args}", effectiveTimeout.TotalSeconds, arguments);
            process.Kill();
            return (-1, "gh command timed out");
        }

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
        return (process.ExitCode, output);
    }
}
