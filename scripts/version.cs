#!/usr/bin/env dotnet
#:package NuGet.Versioning@6.13.1
#:package System.CommandLine@2.0.3
#:property PublishAot=false

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NuGet.Versioning;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};

var rootCommand = new RootCommand("Version management tool for copilotd release workflows.");

var stateCommand = new Command("state", "Read version state from the dev draft release.");
var bodyOption = new Option<string?>("--body") { Description = "Release body content to parse." };
var releasesJsonOption = new Option<string?>("--releases-json") { Description = "JSON array of releases for initialization." };
stateCommand.Options.Add(bodyOption);
stateCommand.Options.Add(releasesJsonOption);
stateCommand.SetAction(parseResult => ExecuteHandled(() =>
{
    var body = parseResult.GetValue(bodyOption);
    var releasesJson = parseResult.GetValue(releasesJsonOption);
    VersionState state;

    if (!string.IsNullOrEmpty(body))
    {
        state = ParseStateFromBody(body);
    }
    else if (!string.IsNullOrEmpty(releasesJson))
    {
        state = InitializeFromReleases(releasesJson);
    }
    else
    {
        state = new VersionState("0.0.1", "pre", 1, 0, "none");
    }

    Console.WriteLine(JsonSerializer.Serialize(state, jsonOptions));
}));
rootCommand.Subcommands.Add(stateCommand);

var calculateCommand = new Command("calculate", "Calculate dev and RC versions from state.");
var stateJsonOption = new Option<string>("--state") { Description = "Version state as JSON.", Required = true };
calculateCommand.Options.Add(stateJsonOption);
calculateCommand.SetAction(parseResult => ExecuteHandled(() =>
{
    var stateJson = parseResult.GetValue(stateJsonOption)!;
    var state = JsonSerializer.Deserialize<VersionState>(stateJson, jsonOptions)
        ?? throw new ArgumentException("Invalid state JSON.");

    var versions = CalculateVersions(state);
    Console.WriteLine(JsonSerializer.Serialize(versions, jsonOptions));
}));
rootCommand.Subcommands.Add(calculateCommand);

var advanceCommand = new Command("advance", "Calculate the next state after a release is shipped.");
var advanceStateOption = new Option<string>("--state") { Description = "Current version state as JSON.", Required = true };
var shippedVersionOption = new Option<string>("--shipped-version") { Description = "The version that was just shipped.", Required = true };
advanceCommand.Options.Add(advanceStateOption);
advanceCommand.Options.Add(shippedVersionOption);
advanceCommand.SetAction(parseResult => ExecuteHandled(() =>
{
    var stateJson = parseResult.GetValue(advanceStateOption)!;
    var shippedVersion = parseResult.GetValue(shippedVersionOption)!;
    var state = JsonSerializer.Deserialize<VersionState>(stateJson, jsonOptions)
        ?? throw new ArgumentException("Invalid state JSON.");

    var newState = AdvanceState(state);
    Console.WriteLine(JsonSerializer.Serialize(newState, jsonOptions));
}));
rootCommand.Subcommands.Add(advanceCommand);

var bumpCommand = new Command("bump", "Apply a version bump and/or phase change.");
var bumpStateOption = new Option<string>("--state") { Description = "Current version state as JSON.", Required = true };
var versionBumpOption = new Option<string>("--version-bump") { Description = "Version bump type: auto, patch, minor, major.", Required = true };
var phaseOption = new Option<string>("--phase") { Description = "Target phase: pre, rc, rtm.", Required = true };
var bumpReleasesJsonOption = new Option<string?>("--releases-json") { Description = "JSON array of shipped releases for validation." };
bumpCommand.Options.Add(bumpStateOption);
bumpCommand.Options.Add(versionBumpOption);
bumpCommand.Options.Add(phaseOption);
bumpCommand.Options.Add(bumpReleasesJsonOption);
bumpCommand.SetAction(parseResult => ExecuteHandled(() =>
{
    var stateJson = parseResult.GetValue(bumpStateOption)!;
    var versionBump = parseResult.GetValue(versionBumpOption)!;
    var phase = parseResult.GetValue(phaseOption)!;
    var releasesJson = parseResult.GetValue(bumpReleasesJsonOption);
    var state = JsonSerializer.Deserialize<VersionState>(stateJson, jsonOptions)
        ?? throw new ArgumentException("Invalid state JSON.");

    var result = BumpVersion(state, versionBump, phase, releasesJson);
    if (!result.Valid)
    {
        Fail(result.Reason ?? "Version bump validation failed.");
    }

    Console.WriteLine(JsonSerializer.Serialize(result.NewState, jsonOptions));
}));
rootCommand.Subcommands.Add(bumpCommand);

var validateCommand = new Command("validate", "Check whether a version is valid compared to shipped releases.");
var versionOption = new Option<string>("--version") { Description = "Version to validate.", Required = true };
var validateReleasesJsonOption = new Option<string?>("--releases-json") { Description = "JSON array of shipped releases." };
validateCommand.Options.Add(versionOption);
validateCommand.Options.Add(validateReleasesJsonOption);
validateCommand.SetAction(parseResult => ExecuteHandled(() =>
{
    var version = parseResult.GetValue(versionOption)!;
    var releasesJson = parseResult.GetValue(validateReleasesJsonOption);
    var result = ValidateVersion(version, releasesJson);
    Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));

    if (!result.Valid)
    {
        Environment.Exit(1);
    }
}));
rootCommand.Subcommands.Add(validateCommand);

return rootCommand.Parse(args).Invoke();

static VersionState ParseStateFromBody(string body)
{
    var match = Regex.Match(body, @"<!--\s*VERSION_STATE:\s*([^|]+)\|([^|]+)\|([^|]+)\|([^|]+)\|([^>\s]+)\s*-->");
    if (!match.Success)
    {
        throw new ArgumentException("Could not parse VERSION_STATE from the dev release body. Ensure it still contains a comment like <!-- VERSION_STATE: <base>|<phase>|<phaseNumber>|<devNumber>|<pending> -->.");
    }

    if (!int.TryParse(match.Groups[3].Value.Trim(), out var phaseNumber))
    {
        throw new ArgumentException("VERSION_STATE contains an invalid phase number.");
    }

    if (!int.TryParse(match.Groups[4].Value.Trim(), out var devNumber))
    {
        throw new ArgumentException("VERSION_STATE contains an invalid dev number.");
    }

    return new VersionState(
        match.Groups[1].Value.Trim(),
        match.Groups[2].Value.Trim(),
        phaseNumber,
        devNumber,
        match.Groups[5].Value.Trim());
}

static void ExecuteHandled(Action action)
{
    try
    {
        action();
    }
    catch (ArgumentException ex)
    {
        Fail(ex.Message);
    }
    catch (FormatException ex)
    {
        Fail(ex.Message);
    }
    catch (JsonException ex)
    {
        Fail($"Invalid JSON input: {ex.Message}");
    }
}

static void Fail(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    Environment.Exit(1);
}

static VersionState InitializeFromReleases(string releasesJson)
{
    var releases = JsonSerializer.Deserialize<List<ReleaseInfo>>(releasesJson, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }) ?? [];

    var latestStable = releases
        .Where(r => !r.IsDraft && !r.IsPrerelease)
        .Select(r => r.TagName.TrimStart('v'))
        .Where(v => NuGetVersion.TryParse(v, out _))
        .Select(NuGetVersion.Parse)
        .OrderByDescending(v => v)
        .FirstOrDefault();

    if (latestStable is null)
    {
        return new VersionState("0.0.1", "pre", 1, 0, "none");
    }

    var nextVersion = latestStable.Major == 0
        ? new NuGetVersion(latestStable.Major, latestStable.Minor, latestStable.Patch + 1)
        : new NuGetVersion(latestStable.Major, latestStable.Minor + 1, 0);

    return new VersionState(nextVersion.ToNormalizedString(), "pre", 1, 0, "none");
}

static CalculatedVersions CalculateVersions(VersionState state)
{
    var devNumber = state.DevNumber + 1;
    var devVersion = state.Phase switch
    {
        "pre" => $"{state.Base}-pre.{state.PhaseNumber}.dev.{devNumber}",
        "rc" => $"{state.Base}-rc.{state.PhaseNumber}.dev.{devNumber}",
        "rtm" => $"{state.Base}-rtm.dev.{devNumber}",
        _ => throw new ArgumentException($"Unknown phase: {state.Phase}")
    };

    var rcVersion = state.Phase switch
    {
        "pre" => $"{state.Base}-pre.{state.PhaseNumber}.rel",
        "rc" => $"{state.Base}-rc.{state.PhaseNumber}.rel",
        "rtm" => state.Base,
        _ => throw new ArgumentException($"Unknown phase: {state.Phase}")
    };

    var nextState = $"{state.Base}|{state.Phase}|{state.PhaseNumber}|{devNumber}|none";
    return new CalculatedVersions(devVersion, rcVersion, nextState);
}

static VersionState AdvanceState(VersionState state)
{
    return state.Phase switch
    {
        "pre" or "rc" => new VersionState(state.Base, state.Phase, state.PhaseNumber + 1, 0, "none"),
        "rtm" => new VersionState(BumpBaseVersion(state.Base), "pre", 1, 0, "none"),
        _ => throw new ArgumentException($"Unknown phase: {state.Phase}")
    };
}

static BumpResult BumpVersion(VersionState state, string versionBump, string targetPhase, string? releasesJson)
{
    if (targetPhase is not ("pre" or "rc" or "rtm"))
    {
        return new BumpResult(false, $"Unknown phase: {targetPhase}.", null);
    }

    var currentOrder = PhaseOrder(state.Phase);
    var targetOrder = PhaseOrder(targetPhase);
    var newBase = state.Base;

    switch (versionBump.ToLowerInvariant())
    {
        case "auto":
            if (targetOrder <= currentOrder)
            {
                newBase = BumpBaseVersion(state.Base);
            }
            break;
        case "patch":
        case "minor":
        case "major":
            newBase = BumpBaseVersion(state.Base, versionBump.ToLowerInvariant());
            break;
        default:
            return new BumpResult(false, $"Unknown version bump type: {versionBump}.", null);
    }

    if (newBase == state.Base && targetPhase == state.Phase)
    {
        return new BumpResult(false, "No effective change was requested.", null);
    }

    var newState = new VersionState(newBase, targetPhase, targetPhase == "rtm" ? 0 : 1, 0, "none");
    var proposedReleaseVersion = targetPhase == "rtm"
        ? newBase
        : $"{newBase}-{targetPhase}.1.rel";

    var validation = ValidateVersion(proposedReleaseVersion, releasesJson);
    if (!validation.Valid)
    {
        return new BumpResult(false, validation.Reason, null);
    }

    return new BumpResult(true, null, newState);
}

static ValidationResult ValidateVersion(string version, string? releasesJson)
{
    if (!NuGetVersion.TryParse(version, out var proposedVersion))
    {
        return new ValidationResult(false, $"Invalid version format: {version}");
    }

    if (string.IsNullOrEmpty(releasesJson))
    {
        return new ValidationResult(true, null);
    }

    var releases = JsonSerializer.Deserialize<List<ReleaseInfo>>(releasesJson, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }) ?? [];

    var shippedVersions = releases
        .Where(r => !r.IsDraft)
        .Select(r => r.TagName.TrimStart('v'))
        .Where(v => NuGetVersion.TryParse(v, out _))
        .Select(NuGetVersion.Parse)
        .ToList();

    foreach (var shipped in shippedVersions)
    {
        if (proposedVersion <= shipped)
        {
            return new ValidationResult(false, $"Version {version} is not greater than existing release {shipped}.");
        }
    }

    return new ValidationResult(true, null);
}

static int PhaseOrder(string phase) => phase switch
{
    "pre" => 0,
    "rc" => 1,
    "rtm" => 2,
    _ => throw new ArgumentException($"Unknown phase: {phase}")
};

static string BumpBaseVersion(string version, string bump = "auto")
{
    var parsed = NuGetVersion.Parse(version);
    return bump switch
    {
        "major" => new NuGetVersion(parsed.Major + 1, 0, 0).ToNormalizedString(),
        "minor" => new NuGetVersion(parsed.Major, parsed.Minor + 1, 0).ToNormalizedString(),
        "patch" => new NuGetVersion(parsed.Major, parsed.Minor, parsed.Patch + 1).ToNormalizedString(),
        _ => parsed.Major == 0
            ? new NuGetVersion(parsed.Major, parsed.Minor, parsed.Patch + 1).ToNormalizedString()
            : new NuGetVersion(parsed.Major, parsed.Minor + 1, 0).ToNormalizedString()
    };
}

internal sealed record VersionState(
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("phaseNumber")] int PhaseNumber,
    [property: JsonPropertyName("devNumber")] int DevNumber,
    [property: JsonPropertyName("pending")] string Pending);

internal sealed record CalculatedVersions(
    [property: JsonPropertyName("devVersion")] string DevVersion,
    [property: JsonPropertyName("rcVersion")] string RcVersion,
    [property: JsonPropertyName("nextState")] string NextState);

internal sealed record BumpResult(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("newState")] VersionState? NewState);

internal sealed record ValidationResult(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("reason")] string? Reason);

internal sealed record ReleaseInfo(
    [property: JsonPropertyName("tagName")] string TagName,
    [property: JsonPropertyName("isDraft")] bool IsDraft,
    [property: JsonPropertyName("isPrerelease")] bool IsPrerelease);
