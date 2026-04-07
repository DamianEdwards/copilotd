using System.Reflection;
using System.Text.RegularExpressions;

namespace Copilotd.Infrastructure;

/// <summary>
/// Semantic version parsing, comparison, and channel classification.
/// Mirrors the logic in install-copilotd.ps1's Compare-SemanticVersion and Parse-SemanticVersion.
/// </summary>
public static partial class VersionHelper
{
    /// <summary>
    /// Parsed semantic version with optional pre-release suffix.
    /// </summary>
    public sealed record SemanticVersion(int Major, int Minor, int Patch, string PreRelease = "")
    {
        public override string ToString() =>
            string.IsNullOrEmpty(PreRelease) ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
    }

    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.\d+)?(?:-(?<prerelease>[0-9A-Za-z.\-]+))?$")]
    private static partial Regex SemVerPattern();

    /// <summary>
    /// Attempts to parse a version string into a <see cref="SemanticVersion"/>.
    /// Accepts formats like "0.0.1", "0.0.1-pre.1.dev.1", "0.0.1.0-rc.1.rel".
    /// </summary>
    public static bool TryParse(string? input, out SemanticVersion version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        // Strip leading 'v' prefix common in git tags (e.g. "v0.0.1")
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        var match = SemVerPattern().Match(trimmed);
        if (!match.Success)
            return false;

        version = new SemanticVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            match.Groups["prerelease"].Value);
        return true;
    }

    /// <summary>
    /// Compares two semantic versions following SemVer 2.0 precedence rules.
    /// Returns negative if left &lt; right, zero if equal, positive if left &gt; right.
    /// </summary>
    public static int Compare(SemanticVersion left, SemanticVersion right)
    {
        var major = left.Major.CompareTo(right.Major);
        if (major != 0) return major;

        var minor = left.Minor.CompareTo(right.Minor);
        if (minor != 0) return minor;

        var patch = left.Patch.CompareTo(right.Patch);
        if (patch != 0) return patch;

        var leftPre = left.PreRelease;
        var rightPre = right.PreRelease;

        // Both have no pre-release: equal
        if (string.IsNullOrEmpty(leftPre) && string.IsNullOrEmpty(rightPre))
            return 0;

        // No pre-release > any pre-release (stable is higher)
        if (string.IsNullOrEmpty(leftPre)) return 1;
        if (string.IsNullOrEmpty(rightPre)) return -1;

        // Compare pre-release identifiers per SemVer 2.0
        var leftIds = leftPre.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightIds = rightPre.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var maxLen = Math.Max(leftIds.Length, rightIds.Length);

        for (var i = 0; i < maxLen; i++)
        {
            if (i >= leftIds.Length) return -1;  // fewer identifiers = lower precedence
            if (i >= rightIds.Length) return 1;

            var leftId = leftIds[i];
            var rightId = rightIds[i];
            var leftIsNum = long.TryParse(leftId, out var leftNum);
            var rightIsNum = long.TryParse(rightId, out var rightNum);

            if (leftIsNum && rightIsNum)
            {
                var cmp = leftNum.CompareTo(rightNum);
                if (cmp != 0) return cmp;
                continue;
            }

            // Numeric identifiers have lower precedence than alphanumeric
            if (leftIsNum) return -1;
            if (rightIsNum) return 1;

            var strCmp = string.CompareOrdinal(leftId, rightId);
            if (strCmp != 0) return strCmp;
        }

        return 0;
    }

    /// <summary>Returns true if the version is a dev build (contains ".dev." in pre-release).</summary>
    public static bool IsDevBuild(SemanticVersion version) =>
        version.PreRelease.Contains(".dev.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true if the version is a pre-release but not a dev build.</summary>
    public static bool IsPreReleaseBuild(SemanticVersion version) =>
        !string.IsNullOrEmpty(version.PreRelease) && !IsDevBuild(version);

    /// <summary>Returns true if the version has no pre-release suffix (stable).</summary>
    public static bool IsStableBuild(SemanticVersion version) =>
        string.IsNullOrEmpty(version.PreRelease);

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is a valid update candidate
    /// for <paramref name="current"/>, applying channel-aware filtering rules:
    /// <list type="bullet">
    ///   <item>Dev builds → any newer version is a candidate</item>
    ///   <item>Preview builds → newer preview or stable versions</item>
    ///   <item>Stable builds → newer stable only (unless <paramref name="allowPreRelease"/>)</item>
    /// </list>
    /// </summary>
    public static bool IsUpdateCandidate(SemanticVersion current, SemanticVersion candidate, bool allowPreRelease)
    {
        // Candidate must be strictly newer
        if (Compare(candidate, current) <= 0)
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
