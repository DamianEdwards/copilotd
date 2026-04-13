using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Verifies provenance of downloaded binaries.
/// Windows: Authenticode signature + certificate chain via embedded PowerShell script.
/// Linux/macOS: GitHub artifact attestations via <c>gh attestation verify</c>.
/// Also handles SHA256 checksum and release-metadata.json validation (cross-platform).
/// </summary>
public sealed class ProvenanceVerifier
{
    private readonly ILogger<ProvenanceVerifier> _logger;
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(60);

    public ProvenanceVerifier(ILogger<ProvenanceVerifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verifies provenance of a binary file.
    /// On Windows: Authenticode signature and certificate chain via embedded PowerShell script.
    /// On Linux/macOS: GitHub artifact attestation via <c>gh attestation verify</c>.
    /// </summary>
    /// <returns>True if verification passed, false if it failed.</returns>
    public async Task<(bool Success, string? Error)> VerifyBinaryTrustAsync(string binaryPath, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return await VerifyAuthenticodeAsync(binaryPath, ct);

        return await VerifyAttestationAsync(binaryPath, ct);
    }

    /// <summary>
    /// Verifies GitHub artifact attestation for a file using <c>gh attestation verify</c>.
    /// Used on Linux/macOS where Authenticode is not available.
    /// Mirrors the verification done by <c>install-copilotd.sh</c>.
    /// Tries both the CI and bump-version workflows since either may have produced the release.
    /// </summary>
    public async Task<(bool Success, string? Error)> VerifyAttestationAsync(string filePath, CancellationToken ct)
    {
        _logger.LogInformation("Verifying artifact attestation for '{FilePath}'", filePath);

        var repo = GitHubReleaseService.Repository;

        // Try each workflow that produces attested release assets
        string[] signerWorkflows =
        [
            $"{repo}/.github/workflows/ci.yml",
            $"{repo}/.github/workflows/bump-version.yml",
        ];

        string? lastError = null;
        foreach (var workflow in signerWorkflows)
        {
            var (success, error) = await RunAttestationVerifyAsync(filePath, repo, workflow, ct);
            if (success)
                return (true, null);
            lastError = error;
            _logger.LogDebug("Attestation verification with workflow '{Workflow}' did not match, trying next", workflow);
        }

        _logger.LogWarning("Attestation verification failed for '{FilePath}': {Error}", filePath, lastError);
        return (false, lastError ?? "Attestation verification failed");
    }

    private async Task<(bool Success, string? Error)> RunAttestationVerifyAsync(
        string filePath, string repo, string signerWorkflow, CancellationToken ct)
    {
        var args = $"attestation verify \"{filePath}\""
            + $" -R {repo}"
            + $" --signer-workflow {signerWorkflow}"
            + " --source-ref refs/heads/main";

        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(VerifyTimeout);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start gh process");
        }
        catch (Exception ex)
        {
            var msg = $"Failed to start gh CLI for attestation verification: {ex.Message}";
            _logger.LogWarning("{Message}", msg);
            return (false, msg);
        }

        using (process)
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Attestation verification timed out after {Timeout}s", VerifyTimeout.TotalSeconds);
                process.Kill();
                return (false, "Attestation verification timed out");
            }

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Artifact attestation verification passed for '{FilePath}'", filePath);
                return (true, null);
            }

            var stderr = await stderrTask;
            var stdout = await stdoutTask;
            var error = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                : "Attestation verification failed";

            return (false, error);
        }
    }

    /// <summary>
    /// Verifies the Authenticode signature and certificate chain of a binary (Windows only)
    /// by writing the embedded verify-provenance.ps1 to a temp file and executing it.
    /// </summary>
    private async Task<(bool Success, string? Error)> VerifyAuthenticodeAsync(string binaryPath, CancellationToken ct)
    {
        var scriptContent = GetEmbeddedScript();
        if (scriptContent is null)
            return (false, "Failed to load embedded verification script");

        _logger.LogInformation("Verifying Authenticode provenance of '{BinaryPath}'", binaryPath);

        // Write the embedded script to a temp file for execution. We use a temp file
        // because -EncodedCommand exceeds the 32K command-line limit and -Command -
        // (stdin) can't reliably parse complex multi-line scripts with Add-Type.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"copilotd-verify-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, scriptContent, ct);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -BinaryPath \"{binaryPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(VerifyTimeout);

            using var process = Process.Start(psi)!;
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Provenance verification timed out after {Timeout}s", VerifyTimeout.TotalSeconds);
                process.Kill();
                return (false, "Provenance verification timed out");
            }

            var stdout = await stdoutTask;

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Provenance verification passed for '{BinaryPath}'", binaryPath);
                return (true, null);
            }

            // Try to parse JSON error from stdout
            var error = "Provenance verification failed";
            try
            {
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    using var doc = JsonDocument.Parse(stdout);
                    if (doc.RootElement.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                        error = errorEl.GetString() ?? error;
                }
            }
            catch
            {
                // Fall back to stderr
                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                    error = stderr.Trim();
            }

            _logger.LogWarning("Provenance verification failed for '{BinaryPath}': {Error}", binaryPath, error);
            return (false, error);
        }
        finally
        {
            try { File.Delete(scriptPath); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Verifies that a file's SHA256 hash matches an expected value from checksums.txt.
    /// </summary>
    public (bool Success, string? Error) VerifyChecksum(string filePath, string checksumsPath, string assetName)
    {
        _logger.LogDebug("Verifying SHA256 checksum for '{AssetName}'", assetName);

        string expectedHash;
        try
        {
            var lines = File.ReadAllLines(checksumsPath);
            expectedHash = ParseExpectedHash(lines, assetName);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to read checksums.txt: {ex.Message}");
        }

        string actualHash;
        try
        {
            using var stream = File.OpenRead(filePath);
            var hashBytes = SHA256.HashData(stream);
            actualHash = Convert.ToHexStringLower(hashBytes);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to compute SHA256 hash: {ex.Message}");
        }

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            var msg = $"SHA256 mismatch for '{assetName}'. Expected '{expectedHash}' but got '{actualHash}'.";
            _logger.LogWarning("{Message}", msg);
            return (false, msg);
        }

        _logger.LogDebug("SHA256 checksum verified for '{AssetName}'", assetName);
        return (true, null);
    }

    /// <summary>
    /// Validates that release-metadata.json agrees with checksums.txt for a given asset.
    /// </summary>
    public (bool Success, string? Error) ValidateReleaseMetadata(string metadataPath, string assetName, string expectedSha256)
    {
        _logger.LogDebug("Validating release metadata for '{AssetName}'", assetName);

        try
        {
            var json = File.ReadAllText(metadataPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("assets", out var assets))
                return (false, "release-metadata.json does not contain 'assets' array");

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameEl) &&
                    string.Equals(nameEl.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!asset.TryGetProperty("sha256", out var sha256El))
                        return (false, $"release-metadata.json asset '{assetName}' missing sha256 field");

                    var metadataSha = sha256El.GetString()?.ToLowerInvariant();
                    if (!string.Equals(metadataSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        return (false, $"release-metadata.json SHA256 for '{assetName}' ({metadataSha}) does not match checksums.txt ({expectedSha256})");

                    _logger.LogDebug("Release metadata validated for '{AssetName}'", assetName);
                    return (true, null);
                }
            }

            return (false, $"release-metadata.json did not contain asset '{assetName}'");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to parse release-metadata.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the embedded verify-provenance.ps1 resource and returns its content as a string.
    /// </summary>
    private string? GetEmbeddedScript()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("verify-provenance.ps1", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                _logger.LogError("Embedded verify-provenance.ps1 resource not found");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read embedded verification script");
            return null;
        }
    }

    private static string ParseExpectedHash(string[] lines, string assetName)
    {
        foreach (var line in lines)
        {
            // Format: "<sha256hash>  <filename>" or "<sha256hash> *<filename>"
            if (!line.Contains(assetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split([' ', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].Equals(assetName, StringComparison.OrdinalIgnoreCase)
                && parts[0].Length == 64)
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new InvalidOperationException($"checksums.txt did not contain an entry for '{assetName}'.");
    }
}
