using System.Text.Json;
using System.Text.Json.Serialization;

namespace Copilotd.Models;

/// <summary>
/// AOT-compatible JSON converter for <see cref="PromptMode"/> that gracefully handles
/// unknown enum values by falling back to <see cref="PromptMode.Append"/>.
/// This prevents a typo or future enum value in config.json from discarding
/// the entire configuration.
/// </summary>
public sealed class TolerantPromptModeConverter : JsonConverter<PromptMode>
{
    public override PromptMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<PromptMode>(value, ignoreCase: true, out var mode))
                return mode;

            return PromptMode.Append;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var intValue = reader.GetInt32();
            if (Enum.IsDefined(typeof(PromptMode), intValue))
                return (PromptMode)intValue;

            return PromptMode.Append;
        }

        return PromptMode.Append;
    }

    public override void Write(Utf8JsonWriter writer, PromptMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// AOT-compatible JSON converter for <see cref="SessionStatus"/> that gracefully handles
/// unknown enum values by falling back to <see cref="SessionStatus.Pending"/>.
/// This prevents older binaries from losing all persisted state when encountering
/// new enum values added in later versions.
/// </summary>
public sealed class TolerantSessionStatusConverter : JsonConverter<SessionStatus>
{
    public override SessionStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<SessionStatus>(value, ignoreCase: true, out var status))
                return status;

            // Unknown enum value from a newer version. Falling back to Pending means
            // the reconciliation engine may re-dispatch the session prematurely, but
            // that is recoverable. The alternatives (Completed = session lost, throwing
            // = entire state file rejected) are worse. The newer binary will correct the
            // status on its next run.
            return SessionStatus.Pending;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var intValue = reader.GetInt32();
            if (Enum.IsDefined(typeof(SessionStatus), intValue))
                return (SessionStatus)intValue;

            return SessionStatus.Pending;
        }

        return SessionStatus.Pending;
    }

    public override void Write(Utf8JsonWriter writer, SessionStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// AOT-compatible JSON converter for <see cref="ReactionTargetType"/> that gracefully handles
/// unknown enum values by falling back to <see cref="ReactionTargetType.IssueBody"/>.
/// </summary>
public sealed class TolerantReactionTargetTypeConverter : JsonConverter<ReactionTargetType>
{
    public override ReactionTargetType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<ReactionTargetType>(value, ignoreCase: true, out var targetType))
                return targetType;

            return ReactionTargetType.IssueBody;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var intValue = reader.GetInt32();
            if (Enum.IsDefined(typeof(ReactionTargetType), intValue))
                return (ReactionTargetType)intValue;

            return ReactionTargetType.IssueBody;
        }

        return ReactionTargetType.IssueBody;
    }

    public override void Write(Utf8JsonWriter writer, ReactionTargetType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// AOT-compatible JSON converter for <see cref="UpdateStatus"/> that gracefully handles
/// unknown enum values by falling back to <see cref="UpdateStatus.None"/>.
/// </summary>
public sealed class TolerantUpdateStatusConverter : JsonConverter<UpdateStatus>
{
    public override UpdateStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<UpdateStatus>(value, ignoreCase: true, out var status))
                return status;
            return UpdateStatus.None;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var intValue = reader.GetInt32();
            if (Enum.IsDefined(typeof(UpdateStatus), intValue))
                return (UpdateStatus)intValue;
            return UpdateStatus.None;
        }

        return UpdateStatus.None;
    }

    public override void Write(Utf8JsonWriter writer, UpdateStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// AOT-compatible JSON converter for <see cref="CommentTrustLevel"/> that gracefully handles
/// unknown enum values by falling back to <see cref="CommentTrustLevel.Collaborators"/>.
/// </summary>
public sealed class TolerantCommentTrustLevelConverter : JsonConverter<CommentTrustLevel>
{
    public override CommentTrustLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<CommentTrustLevel>(value, ignoreCase: true, out var level))
                return level;

            return CommentTrustLevel.Collaborators;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var intValue = reader.GetInt32();
            if (Enum.IsDefined(typeof(CommentTrustLevel), intValue))
                return (CommentTrustLevel)intValue;

            return CommentTrustLevel.Collaborators;
        }

        return CommentTrustLevel.Collaborators;
    }

    public override void Write(Utf8JsonWriter writer, CommentTrustLevel value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// AOT-compatible JSON converter for <see cref="AuthorMode"/> that gracefully handles
/// unknown enum values by falling back to <see cref="AuthorMode.Any"/>.
/// </summary>
public sealed class TolerantAuthorModeConverter : JsonConverter<AuthorMode>
{
    public override AuthorMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<AuthorMode>(value, ignoreCase: true, out var mode))
                return mode;

            return AuthorMode.Any;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var intValue = reader.GetInt32();
            if (Enum.IsDefined(typeof(AuthorMode), intValue))
                return (AuthorMode)intValue;

            return AuthorMode.Any;
        }

        return AuthorMode.Any;
    }

    public override void Write(Utf8JsonWriter writer, AuthorMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// AOT-compatible JSON converter for <see cref="ControlSessionStatus"/> that gracefully handles
/// unknown enum values by falling back to <see cref="ControlSessionStatus.Stopped"/>.
/// </summary>
public sealed class TolerantControlSessionStatusConverter : JsonConverter<ControlSessionStatus>
{
    public override ControlSessionStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<ControlSessionStatus>(value, ignoreCase: true, out var status))
                return status;

            return ControlSessionStatus.Stopped;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var intValue = reader.GetInt32();
            if (Enum.IsDefined(typeof(ControlSessionStatus), intValue))
                return (ControlSessionStatus)intValue;

            return ControlSessionStatus.Stopped;
        }

        return ControlSessionStatus.Stopped;
    }

    public override void Write(Utf8JsonWriter writer, ControlSessionStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// AOT-safe JSON serialization metadata for all persisted models.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(TolerantSessionStatusConverter), typeof(TolerantReactionTargetTypeConverter), typeof(TolerantUpdateStatusConverter), typeof(TolerantPromptModeConverter), typeof(TolerantCommentTrustLevelConverter), typeof(TolerantAuthorModeConverter), typeof(TolerantControlSessionStatusConverter)])]
[JsonSerializable(typeof(CopilotdConfig))]
[JsonSerializable(typeof(DaemonState))]
[JsonSerializable(typeof(DispatchRule))]
[JsonSerializable(typeof(DispatchSession))]
[JsonSerializable(typeof(ControlSessionInfo))]
[JsonSerializable(typeof(GitHubIssue))]
[JsonSerializable(typeof(List<GitHubIssue>))]
[JsonSerializable(typeof(List<GhRepo>))]
[JsonSerializable(typeof(GhAuthStatus))]
[JsonSerializable(typeof(UpdateState))]
public partial class CopilotdJsonContext : JsonSerializerContext;

/// <summary>
/// Represents a repo from gh repo list --json.
/// </summary>
public sealed class GhRepo
{
    [JsonPropertyName("nameWithOwner")]
    public string NameWithOwner { get; set; } = "";
}

/// <summary>
/// Represents gh auth status --json output (simplified).
/// </summary>
public sealed class GhAuthStatus
{
    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }
}
