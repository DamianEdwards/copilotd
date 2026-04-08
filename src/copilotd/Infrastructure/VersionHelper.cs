using System.Reflection;
using NuGet.Versioning;

namespace Copilotd.Infrastructure;

/// <summary>
/// Semantic version parsing, comparison, and channel classification
/// using NuGet.Versioning for SemVer 2.0 compliance.
/// </summary>
public static class VersionHelper
{
    /// <summary>
    /// Attempts to parse a version string into a <see cref="NuGetVersion"/>.
    /// Accepts formats like "0.0.1", "0.0.1-pre.1.dev.1", "v0.0.1-rc.1".
    /// </summary>
    public static bool TryParse(string? input, out NuGetVersion version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        // Strip leading 'v' prefix common in git tags (e.g. "v0.0.1")
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        if (NuGetVersion.TryParse(trimmed, out var parsed))
        {
            version = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Returns true if the version is a dev build (contains "dev" label in pre-release).</summary>
    public static bool IsDevBuild(NuGetVersion version) =>
        version.IsPrerelease && version.ReleaseLabels.Any(l =>
            string.Equals(l, "dev", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns true if the version is a pre-release but not a dev build.</summary>
    public static bool IsPreReleaseBuild(NuGetVersion version) =>
        version.IsPrerelease && !IsDevBuild(version);

    /// <summary>Returns true if the version has no pre-release suffix (stable).</summary>
    public static bool IsStableBuild(NuGetVersion version) =>
        !version.IsPrerelease;

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is a valid update candidate
    /// for <paramref name="current"/>, applying channel-aware filtering rules:
    /// <list type="bullet">
    ///   <item>Dev builds → any newer version is a candidate</item>
    ///   <item>Preview builds → newer preview or stable versions</item>
    ///   <item>Stable builds → newer stable only (unless <paramref name="allowPreRelease"/>)</item>
    /// </list>
    /// </summary>
    public static bool IsUpdateCandidate(NuGetVersion current, NuGetVersion candidate, bool allowPreRelease)
    {
        // Candidate must be strictly newer
        if (candidate.CompareTo(current) <= 0)
            return false;

        // Dev builds accept anything newer
        if (IsDevBuild(current))
            return true;

        // Preview builds accept newer preview or stable (but not dev)
        if (IsPreReleaseBuild(current))
            return !IsDevBuild(candidate);

        // Stable builds: only stable, unless pre-release flag is set
        if (allowPreRelease)
            return !IsDevBuild(candidate);

        return IsStableBuild(candidate);
    }

    /// <summary>
    /// Gets the version of the currently running binary from assembly metadata.
    /// </summary>
    public static string? GetCurrentVersion()
    {
        var attr = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr?.InformationalVersion is { } version)
        {
            // Strip build metadata ("+commitsha" suffix)
            var plusIdx = version.IndexOf('+');
            return plusIdx >= 0 ? version[..plusIdx] : version;
        }

        var asm = Assembly.GetEntryAssembly()?.GetName().Version;
        return asm?.ToString(3); // Major.Minor.Patch
    }
}
