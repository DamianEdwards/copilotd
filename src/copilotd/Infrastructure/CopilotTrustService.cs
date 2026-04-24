using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

public enum CopilotTrustStatus
{
    Trusted,
    Untrusted,
    Unknown,
}

public sealed class CopilotTrustCheckResult
{
    public CopilotTrustStatus Status { get; init; }
    public IReadOnlyList<string> RequiredFolders { get; init; } = [];
    public IReadOnlyList<string> MissingFolders { get; init; } = [];
    public string? Message { get; init; }
}

public sealed class CopilotTrustMutationResult
{
    public bool Succeeded { get; init; }
    public IReadOnlyList<string> AddedFolders { get; init; } = [];
    public string? Message { get; init; }
}

public sealed class CopilotTrustService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private readonly string _configPath;
    private readonly ILogger<CopilotTrustService> _logger;

    public CopilotTrustService(ILogger<CopilotTrustService> logger)
        : this(CopilotdPaths.GetCopilotConfigPath(), logger)
    {
    }

    public CopilotTrustService(string configPath, ILogger<CopilotTrustService> logger)
    {
        _configPath = configPath;
        _logger = logger;
    }

    public string ConfigPath => _configPath;

    public IReadOnlyList<string> GetRequiredTrustedFolders(string repoPath)
    {
        var normalizedRepoPath = NormalizePath(repoPath);
        return
        [
            normalizedRepoPath,
            NormalizePath(normalizedRepoPath + "_sessions")
        ];
    }

    public CopilotTrustCheckResult CheckTrustedFolders(IEnumerable<string> requiredFolders)
    {
        var normalizedRequiredFolders = NormalizeFolders(requiredFolders);
        if (normalizedRequiredFolders.Count == 0)
        {
            return new CopilotTrustCheckResult
            {
                Status = CopilotTrustStatus.Trusted,
                RequiredFolders = normalizedRequiredFolders,
            };
        }

        ConfigReadResult? lastResult = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            lastResult = TryReadConfigOnce();
            switch (lastResult.Status)
            {
                case ConfigReadStatus.Success:
                    var missingFolders = normalizedRequiredFolders
                        .Where(requiredFolder => !IsTrustedByAny(requiredFolder, lastResult.TrustedFolders))
                        .ToList();

                    return new CopilotTrustCheckResult
                    {
                        Status = missingFolders.Count == 0 ? CopilotTrustStatus.Trusted : CopilotTrustStatus.Untrusted,
                        RequiredFolders = normalizedRequiredFolders,
                        MissingFolders = missingFolders,
                    };

                case ConfigReadStatus.Missing:
                case ConfigReadStatus.UnsupportedSchema:
                    return CreateUnknownResult(normalizedRequiredFolders, lastResult.Message);

                case ConfigReadStatus.RetryableFailure:
                    if (attempt < MaxAttempts)
                    {
                        _logger.LogDebug("Retrying Copilot config trust check for {Path} (attempt {Attempt}/{Max})",
                            ConfigPath, attempt + 1, MaxAttempts);
                        Thread.Sleep(RetryDelay);
                        continue;
                    }

                    return CreateUnknownResult(normalizedRequiredFolders, lastResult.Message);
            }
        }

        return CreateUnknownResult(normalizedRequiredFolders,
            lastResult?.Message ?? "Copilot folder trust could not be verified.");
    }

    public CopilotTrustMutationResult AddTrustedFolders(IEnumerable<string> folders)
    {
        var normalizedFolders = NormalizeFolders(folders);
        if (normalizedFolders.Count == 0)
        {
            return new CopilotTrustMutationResult
            {
                Succeeded = true,
            };
        }

        ConfigReadResult? lastReadResult = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            lastReadResult = TryReadConfigOnce();
            switch (lastReadResult.Status)
            {
                case ConfigReadStatus.Success:
                    var foldersToAdd = normalizedFolders
                        .Where(folder => !IsTrustedByAny(folder, lastReadResult.TrustedFolders))
                        .ToList();
                    if (foldersToAdd.Count == 0)
                    {
                        return new CopilotTrustMutationResult
                        {
                            Succeeded = true,
                        };
                    }

                    var trustedFolders = (JsonArray)lastReadResult.Root!["trustedFolders"]!;
                    foreach (var folder in foldersToAdd)
                        trustedFolders.Add((JsonNode?)JsonValue.Create(folder));

                    var writeResult = TryWriteConfigOnce(lastReadResult.Root!);
                    if (writeResult.Succeeded)
                    {
                        return new CopilotTrustMutationResult
                        {
                            Succeeded = true,
                            AddedFolders = foldersToAdd,
                        };
                    }

                    if (attempt < MaxAttempts)
                    {
                        _logger.LogDebug("Retrying Copilot config trust update for {Path} (attempt {Attempt}/{Max})",
                            ConfigPath, attempt + 1, MaxAttempts);
                        Thread.Sleep(RetryDelay);
                        continue;
                    }

                    return new CopilotTrustMutationResult
                    {
                        Succeeded = false,
                        Message = writeResult.Message,
                    };

                case ConfigReadStatus.Missing:
                case ConfigReadStatus.UnsupportedSchema:
                    return new CopilotTrustMutationResult
                    {
                        Succeeded = false,
                        Message = lastReadResult.Message,
                    };

                case ConfigReadStatus.RetryableFailure:
                    if (attempt < MaxAttempts)
                    {
                        _logger.LogDebug("Retrying Copilot config trust update for {Path} (attempt {Attempt}/{Max})",
                            ConfigPath, attempt + 1, MaxAttempts);
                        Thread.Sleep(RetryDelay);
                        continue;
                    }

                    return new CopilotTrustMutationResult
                    {
                        Succeeded = false,
                        Message = lastReadResult.Message,
                    };
            }
        }

        return new CopilotTrustMutationResult
        {
            Succeeded = false,
            Message = lastReadResult?.Message ?? "Copilot trusted folders could not be updated.",
        };
    }

    private ConfigReadResult TryReadConfigOnce()
    {
        if (!File.Exists(ConfigPath))
        {
            return new ConfigReadResult
            {
                Status = ConfigReadStatus.Missing,
                Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' does not exist yet.",
            };
        }

        try
        {
            using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json))
                throw new JsonException("Copilot config file is empty.");

            var node = JsonNode.Parse(json, nodeOptions: null, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is not JsonObject root)
            {
                return new ConfigReadResult
                {
                    Status = ConfigReadStatus.UnsupportedSchema,
                    Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' is not a JSON object.",
                };
            }

            if (root["trustedFolders"] is not JsonArray trustedFoldersNode)
            {
                return new ConfigReadResult
                {
                    Status = ConfigReadStatus.UnsupportedSchema,
                    Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' does not contain the expected 'trustedFolders' array.",
                };
            }

            var trustedFolders = new List<string>();
            foreach (var item in trustedFoldersNode)
            {
                if (item is not JsonValue jsonValue)
                {
                    return new ConfigReadResult
                    {
                        Status = ConfigReadStatus.UnsupportedSchema,
                        Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' has a non-string entry in 'trustedFolders'.",
                    };
                }

                string trustedFolder;
                try
                {
                    trustedFolder = jsonValue.GetValue<string>();
                }
                catch (Exception)
                {
                    return new ConfigReadResult
                    {
                        Status = ConfigReadStatus.UnsupportedSchema,
                        Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' has a non-string entry in 'trustedFolders'.",
                    };
                }

                if (string.IsNullOrWhiteSpace(trustedFolder))
                {
                    return new ConfigReadResult
                    {
                        Status = ConfigReadStatus.UnsupportedSchema,
                        Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' has an empty entry in 'trustedFolders'.",
                    };
                }

                trustedFolders.Add(NormalizePath(trustedFolder));
            }

            return new ConfigReadResult
            {
                Status = ConfigReadStatus.Success,
                Root = root,
                TrustedFolders = trustedFolders,
            };
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Retryable Copilot config read failure for {Path}", ConfigPath);
            return new ConfigReadResult
            {
                Status = ConfigReadStatus.RetryableFailure,
                Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' is temporarily unavailable: {ex.Message}",
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Retryable Copilot config read failure for {Path}", ConfigPath);
            return new ConfigReadResult
            {
                Status = ConfigReadStatus.RetryableFailure,
                Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' is temporarily unavailable: {ex.Message}",
            };
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Retryable Copilot config parse failure for {Path}", ConfigPath);
            return new ConfigReadResult
            {
                Status = ConfigReadStatus.RetryableFailure,
                Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' could not be read as complete JSON after retries.",
            };
        }
    }

    private ConfigWriteResult TryWriteConfigOnce(JsonObject root)
    {
        var tempPath = Path.Combine(Path.GetDirectoryName(ConfigPath)!,
            $".{Path.GetFileName(ConfigPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);

            return new ConfigWriteResult
            {
                Succeeded = true,
            };
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Retryable Copilot config write failure for {Path}", ConfigPath);
            return new ConfigWriteResult
            {
                Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' could not be updated safely: {ex.Message}",
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Retryable Copilot config write failure for {Path}", ConfigPath);
            return new ConfigWriteResult
            {
                Message = $"Copilot config file '{NormalizeDisplayPath(ConfigPath)}' could not be updated safely: {ex.Message}",
            };
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static CopilotTrustCheckResult CreateUnknownResult(
        IReadOnlyList<string> requiredFolders,
        string? message)
        => new()
        {
            Status = CopilotTrustStatus.Unknown,
            RequiredFolders = requiredFolders,
            Message = message,
        };

    private static List<string> NormalizeFolders(IEnumerable<string> folders)
        => folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizePath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeDisplayPath(string path)
        => NormalizePath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static bool IsTrustedByAny(string requiredFolder, IEnumerable<string> trustedFolders)
        => trustedFolders.Any(trustedFolder => IsSameOrAncestor(trustedFolder, requiredFolder));

    private static bool IsSameOrAncestor(string candidateAncestor, string candidatePath)
    {
        if (string.Equals(candidateAncestor, candidatePath, StringComparison.OrdinalIgnoreCase))
            return true;

        var ancestorWithSeparator = candidateAncestor + Path.DirectorySeparatorChar;
        var pathWithSeparator = candidatePath + Path.DirectorySeparatorChar;
        return pathWithSeparator.StartsWith(ancestorWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private enum ConfigReadStatus
    {
        Success,
        Missing,
        UnsupportedSchema,
        RetryableFailure,
    }

    private sealed class ConfigReadResult
    {
        public ConfigReadStatus Status { get; init; }
        public JsonObject? Root { get; init; }
        public IReadOnlyList<string> TrustedFolders { get; init; } = [];
        public string? Message { get; init; }
    }

    private sealed class ConfigWriteResult
    {
        public bool Succeeded { get; init; }
        public string? Message { get; init; }
    }
}
