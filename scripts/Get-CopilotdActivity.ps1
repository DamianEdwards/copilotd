#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [string]$CopilotdHome,
    [string]$CopilotdPath,
    [int]$RecentSessionCount = 6,
    [int]$RecentEventCount = 8,
    [switch]$IncludeRawStatus,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-CopilotdHome {
    param([string]$ExplicitHome)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitHome))
    {
        return [System.IO.Path]::GetFullPath($ExplicitHome)
    }

    if (-not [string]::IsNullOrWhiteSpace($env:COPILOTD_HOME))
    {
        return [System.IO.Path]::GetFullPath($env:COPILOTD_HOME)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $HOME '.copilotd'))
}

function Resolve-CopilotdExecutable {
    param(
        [string]$ExplicitPath,
        [string]$ResolvedHome
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath))
    {
        return $ExplicitPath
    }

    $installedPath = Join-Path $ResolvedHome 'bin/copilotd'
    if (Test-Path -LiteralPath $installedPath)
    {
        return $installedPath
    }

    $command = Get-Command copilotd -ErrorAction SilentlyContinue
    if ($null -ne $command)
    {
        return $command.Source
    }

    return $installedPath
}

function Read-JsonFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path))
    {
        return $null
    }

    $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($raw))
    {
        return $null
    }

    return $raw | ConvertFrom-Json -Depth 100
}

function Get-OptionalPropertyValue {
    param(
        $Object,
        [string]$Name
    )

    if ($null -eq $Object)
    {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property)
    {
        return $null
    }

    return $property.Value
}

function Get-DateTimeOffsetOrNull {
    param($Value)

    if ($null -eq $Value)
    {
        return $null
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text))
    {
        return $null
    }

    return [DateTimeOffset]::Parse(
        $text,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
}

function Format-AbsoluteTime {
    param($Value)

    if ($null -eq $Value)
    {
        return 'n/a'
    }

    $timestamp = if ($Value -is [DateTimeOffset]) { $Value } else { [DateTimeOffset]$Value }
    return $timestamp.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')
}

function Format-RelativeTime {
    param($Value)

    if ($null -eq $Value)
    {
        return 'n/a'
    }

    $timestamp = if ($Value -is [DateTimeOffset]) { $Value } else { [DateTimeOffset]$Value }
    $span = [DateTimeOffset]::Now - $timestamp.ToLocalTime()
    if ($span.TotalSeconds -lt 0)
    {
        $span = [TimeSpan]::Zero
    }

    if ($span.TotalSeconds -lt 45)
    {
        return 'just now'
    }

    if ($span.TotalMinutes -lt 60)
    {
        return '{0}m ago' -f [int][Math]::Floor($span.TotalMinutes)
    }

    if ($span.TotalHours -lt 24)
    {
        $hours = [int][Math]::Floor($span.TotalHours)
        $minutes = $span.Minutes
        if ($minutes -eq 0)
        {
            return '{0}h ago' -f $hours
        }

        return '{0}h {1}m ago' -f $hours, $minutes
    }

    $days = [int][Math]::Floor($span.TotalDays)
    if ($span.Hours -eq 0)
    {
        return '{0}d ago' -f $days
    }

    return '{0}d {1}h ago' -f $days, $span.Hours
}

function Get-WatchedRepos {
    param($Config)

    $repos = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($collectionName in @('issueRules', 'pullRequestRules'))
    {
        $collection = Get-OptionalPropertyValue -Object $Config -Name $collectionName
        if ($null -eq $collection)
        {
            continue
        }

        foreach ($rule in $collection.PSObject.Properties)
        {
            foreach ($repo in @($rule.Value.repos))
            {
                if (-not [string]::IsNullOrWhiteSpace([string]$repo))
                {
                    $null = $repos.Add([string]$repo)
                }
            }
        }
    }

    return @($repos | Sort-Object)
}

function Get-Sessions {
    param($State)

    if ($null -eq $State -or $null -eq $State.sessions)
    {
        return @()
    }

    $sessions = foreach ($property in $State.sessions.PSObject.Properties)
    {
        $session = $property.Value
        [pscustomobject]@{
            Subject = $property.Name
            SubjectKind = [string](Get-OptionalPropertyValue -Object $session -Name 'subjectKind')
            Status = [string](Get-OptionalPropertyValue -Object $session -Name 'status')
            Title = [string](Get-OptionalPropertyValue -Object $session -Name 'subjectTitle')
            RuleName = [string](Get-OptionalPropertyValue -Object $session -Name 'ruleName')
            UpdatedAt = Get-DateTimeOffsetOrNull (Get-OptionalPropertyValue -Object $session -Name 'updatedAt')
            CreatedAt = Get-DateTimeOffsetOrNull (Get-OptionalPropertyValue -Object $session -Name 'createdAt')
            WaitingSince = Get-DateTimeOffsetOrNull (Get-OptionalPropertyValue -Object $session -Name 'waitingSince')
            LastVerifiedAt = Get-DateTimeOffsetOrNull (Get-OptionalPropertyValue -Object $session -Name 'lastVerifiedAt')
            LastHookEvent = [string](Get-OptionalPropertyValue -Object $session -Name 'lastHookEvent')
            LastHookDetail = [string](Get-OptionalPropertyValue -Object $session -Name 'lastHookDetail')
            ProcessId = if ($null -ne (Get-OptionalPropertyValue -Object $session -Name 'processId')) { [int](Get-OptionalPropertyValue -Object $session -Name 'processId') } else { $null }
            RetryCount = if ($null -ne (Get-OptionalPropertyValue -Object $session -Name 'retryCount')) { [int](Get-OptionalPropertyValue -Object $session -Name 'retryCount') } else { 0 }
            RedispatchCount = if ($null -ne (Get-OptionalPropertyValue -Object $session -Name 'redispatchCount')) { [int](Get-OptionalPropertyValue -Object $session -Name 'redispatchCount') } else { 0 }
            FailureDetail = [string](Get-OptionalPropertyValue -Object $session -Name 'failureDetail')
            WorktreePath = [string](Get-OptionalPropertyValue -Object $session -Name 'worktreePath')
            BaseBranch = [string](Get-OptionalPropertyValue -Object $session -Name 'pullRequestBaseBranch')
            HeadBranch = [string](Get-OptionalPropertyValue -Object $session -Name 'pullRequestHeadBranch')
        }
    }

    return @($sessions | Sort-Object UpdatedAt -Descending)
}

function Get-LatestDaemonLogDirectory {
    param([string]$ResolvedHome)

    $logRoot = Join-Path $ResolvedHome 'logs'
    if (-not (Test-Path -LiteralPath $logRoot))
    {
        return $null
    }

    return Get-ChildItem -LiteralPath $logRoot -Directory -Filter 'daemon_*' |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Get-ParsedLogEntries {
    param($LogDirectory)

    if ($null -eq $LogDirectory)
    {
        return @()
    }

    $entries = foreach ($logFile in (Get-ChildItem -LiteralPath $LogDirectory.FullName -File -Filter '*.log' | Sort-Object Name))
    {
        foreach ($line in Get-Content -LiteralPath $logFile.FullName)
        {
            if ($line -match '^\[(?<timestamp>[^\]]+)\] \[(?<level>[A-Z]+)\] \[(?<source>[^\]]+)\] (?<message>.*)$')
            {
                [pscustomobject]@{
                    Timestamp = [DateTimeOffset]::Parse(
                        $matches.timestamp,
                        [System.Globalization.CultureInfo]::InvariantCulture,
                        [System.Globalization.DateTimeStyles]::AssumeUniversal)
                    Level = $matches.level
                    Source = $matches.source
                    Message = $matches.message
                }
            }
        }
    }

    return @($entries)
}

function Test-IgnoredLogEntry {
    param($Entry)

    if ($Entry.Source -eq 'Copilotd.Services.GhCliService')
    {
        return $true
    }

    if ($Entry.Source -eq 'Copilotd.Infrastructure.StateStore')
    {
        return $true
    }

    if ($Entry.Message -like 'Running: gh *')
    {
        return $true
    }

    if ($Entry.Message -like 'copilot --remote *')
    {
        return $true
    }

    return $false
}

function Get-LastLogEventForSubject {
    param(
        [object[]]$Entries,
        [string]$Subject
    )

    for ($i = $Entries.Count - 1; $i -ge 0; $i--)
    {
        $entry = $Entries[$i]
        if (Test-IgnoredLogEntry $entry)
        {
            continue
        }

        if ($entry.Message -like "*$Subject*" -and $entry.Message -notlike 'Desired PR dispatch:*')
        {
            return $entry
        }
    }

    for ($i = $Entries.Count - 1; $i -ge 0; $i--)
    {
        $entry = $Entries[$i]
        if (Test-IgnoredLogEntry $entry)
        {
            continue
        }

        if ($entry.Message -like "*$Subject*")
        {
            return $entry
        }
    }

    return $null
}

function Get-RecentNotableLogEvents {
    param(
        [object[]]$Entries,
        [int]$Count
    )

    $patterns = @(
        'Queueing ',
        'Launching copilot',
        'Copilot launched',
        'Worktree ',
        'Reconciliation complete',
        'max instances',
        'Skipping large PR split retry',
        'Failed',
        'orphaned'
    )

    $events = foreach ($entry in $Entries)
    {
        if (Test-IgnoredLogEntry $entry)
        {
            continue
        }

        if ($patterns.Where({ $entry.Message -like "*$_*" }, 'First').Count -gt 0)
        {
            $entry
        }
    }

    return @($events | Sort-Object Timestamp -Descending | Select-Object -First $Count)
}

function Get-SessionCounts {
    param([object[]]$Sessions)

    [pscustomobject]@{
        Running = @($Sessions | Where-Object Status -eq 'Running').Count
        Pending = @($Sessions | Where-Object Status -eq 'Pending').Count
        Dispatching = @($Sessions | Where-Object Status -eq 'Dispatching').Count
        WaitingForFeedback = @($Sessions | Where-Object Status -eq 'WaitingForFeedback').Count
        WaitingForReview = @($Sessions | Where-Object Status -eq 'WaitingForReview').Count
        Failed = @($Sessions | Where-Object Status -eq 'Failed').Count
        Orphaned = @($Sessions | Where-Object Status -eq 'Orphaned').Count
        Completed = @($Sessions | Where-Object Status -eq 'Completed').Count
        Terminal = @($Sessions | Where-Object { $_.Status -in @('Completed', 'Failed') }).Count
        Total = $Sessions.Count
    }
}

function Join-Subjects {
    param([object[]]$Sessions)

    $subjects = @($Sessions | ForEach-Object Subject)
    if ($subjects.Count -eq 0)
    {
        return ''
    }

    if ($subjects.Count -eq 1)
    {
        return $subjects[0]
    }

    if ($subjects.Count -eq 2)
    {
        return '{0} and {1}' -f $subjects[0], $subjects[1]
    }

    return '{0}, {1}, and {2} more' -f $subjects[0], $subjects[1], ($subjects.Count - 2)
}

function Get-Headline {
    param(
        $Counts,
        [object[]]$RunningSessions,
        [object[]]$PendingSessions
    )

    if ($Counts.Total -eq 0)
    {
        return 'copilotd is not tracking any sessions right now.'
    }

    if ($RunningSessions.Count -gt 0 -and $PendingSessions.Count -gt 0)
    {
        return 'copilotd is actively working on {0} and has {1} queued next; {2} other PRs are parked in WaitingForReview.' -f `
            (Join-Subjects $RunningSessions),
            (Join-Subjects $PendingSessions),
            $Counts.WaitingForReview
    }

    if ($RunningSessions.Count -gt 0)
    {
        return 'copilotd is actively working on {0}; {1} PRs are parked in WaitingForReview.' -f `
            (Join-Subjects $RunningSessions),
            $Counts.WaitingForReview
    }

    if ($PendingSessions.Count -gt 0)
    {
        return 'copilotd is between runs right now: {0} is queued next, and {1} PRs are parked in WaitingForReview.' -f `
            (Join-Subjects $PendingSessions),
            $Counts.WaitingForReview
    }

    if ($Counts.WaitingForReview -gt 0)
    {
        return 'copilotd is currently monitoring {0} PRs in WaitingForReview and waiting for new review work to appear.' -f $Counts.WaitingForReview
    }

    if ($Counts.WaitingForFeedback -gt 0)
    {
        return 'copilotd is currently waiting on feedback for {0} session(s).' -f $Counts.WaitingForFeedback
    }

    return 'copilotd is idle right now; there are no running or queued sessions.'
}

function Get-RawStatusSnapshot {
    param([string]$ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath))
    {
        return $null
    }

    try
    {
        $output = & $ExecutablePath status 2>&1
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = @($output)
        }
    }
    catch
    {
        return [pscustomobject]@{
            ExitCode = 1
            Output = @($_.Exception.Message)
        }
    }
}

$resolvedHome = Resolve-CopilotdHome -ExplicitHome $CopilotdHome
$resolvedCopilotdPath = Resolve-CopilotdExecutable -ExplicitPath $CopilotdPath -ResolvedHome $resolvedHome

$config = Read-JsonFile -Path (Join-Path $resolvedHome 'config.json')
$state = Read-JsonFile -Path (Join-Path $resolvedHome 'state.json')
$sessions = Get-Sessions -State $state
$counts = Get-SessionCounts -Sessions $sessions
$watchedRepos = if ($null -ne $config) { Get-WatchedRepos -Config $config } else { @() }
$runningSessions = @($sessions | Where-Object Status -eq 'Running')
$pendingSessions = @($sessions | Where-Object Status -eq 'Pending')
$needsAttention = @($sessions | Where-Object { $_.Status -in @('Failed', 'Orphaned') } | Select-Object -First $RecentSessionCount)
$recentWaiting = @(
    $sessions |
        Where-Object { $_.Status -in @('WaitingForReview', 'WaitingForFeedback') } |
        Sort-Object UpdatedAt -Descending |
        Select-Object -First $RecentSessionCount
)

$latestDaemonLogDirectory = Get-LatestDaemonLogDirectory -ResolvedHome $resolvedHome
$parsedLogEntries = Get-ParsedLogEntries -LogDirectory $latestDaemonLogDirectory
$recentEvents = Get-RecentNotableLogEvents -Entries $parsedLogEntries -Count $RecentEventCount
$rawStatus = Get-RawStatusSnapshot -ExecutablePath $resolvedCopilotdPath
$daemonStatus = 'unknown'
if ($null -ne $rawStatus -and $rawStatus.ExitCode -eq 0)
{
    $rawJoined = $rawStatus.Output -join "`n"
    if ($rawJoined -match 'Daemon is running')
    {
        $daemonStatus = 'running'
    }
    elseif ($rawJoined -match 'Daemon is not running')
    {
        $daemonStatus = 'stopped'
    }
}

$summary = [pscustomobject]@{
    Headline = Get-Headline -Counts $counts -RunningSessions $runningSessions -PendingSessions $pendingSessions
    DaemonStatus = $daemonStatus
    LastPollAt = Get-DateTimeOffsetOrNull (Get-OptionalPropertyValue -Object $state -Name 'lastPollTime')
    Home = $resolvedHome
    WatchedRepos = $watchedRepos
    LatestDaemonLogDirectory = if ($null -ne $latestDaemonLogDirectory) { $latestDaemonLogDirectory.FullName } else { $null }
    Counts = $counts
    RunningSessions = @(
        $runningSessions | ForEach-Object {
            $lastEvent = Get-LastLogEventForSubject -Entries $parsedLogEntries -Subject $_.Subject
            [pscustomobject]@{
                Subject = $_.Subject
                Title = $_.Title
                Status = $_.Status
                UpdatedAt = $_.UpdatedAt
                LastEventAt = if ($null -ne $lastEvent) { $lastEvent.Timestamp } else { $null }
                LastEvent = if ($null -ne $lastEvent) { $lastEvent.Message } else { $null }
                ProcessId = $_.ProcessId
            }
        }
    )
    PendingSessions = @(
        $pendingSessions | ForEach-Object {
            $lastEvent = Get-LastLogEventForSubject -Entries $parsedLogEntries -Subject $_.Subject
            [pscustomobject]@{
                Subject = $_.Subject
                Title = $_.Title
                Status = $_.Status
                UpdatedAt = $_.UpdatedAt
                LastEventAt = if ($null -ne $lastEvent) { $lastEvent.Timestamp } else { $null }
                LastEvent = if ($null -ne $lastEvent) { $lastEvent.Message } else { $null }
            }
        }
    )
    RecentWaitingSessions = @(
        $recentWaiting | ForEach-Object {
            $lastEvent = Get-LastLogEventForSubject -Entries $parsedLogEntries -Subject $_.Subject
            [pscustomobject]@{
                Subject = $_.Subject
                Title = $_.Title
                Status = $_.Status
                UpdatedAt = $_.UpdatedAt
                LastHookEvent = $_.LastHookEvent
                LastHookDetail = $_.LastHookDetail
                LastEventAt = if ($null -ne $lastEvent) { $lastEvent.Timestamp } else { $null }
                LastEvent = if ($null -ne $lastEvent) { $lastEvent.Message } else { $null }
            }
        }
    )
    NeedsAttention = @(
        $needsAttention | ForEach-Object {
            [pscustomobject]@{
                Subject = $_.Subject
                Title = $_.Title
                Status = $_.Status
                UpdatedAt = $_.UpdatedAt
                RetryCount = $_.RetryCount
                FailureDetail = $_.FailureDetail
            }
        }
    )
    RecentEvents = @(
        $recentEvents | ForEach-Object {
            [pscustomobject]@{
                Timestamp = $_.Timestamp
                Source = $_.Source
                Message = $_.Message
            }
        }
    )
    RawStatus = if ($IncludeRawStatus) { $rawStatus.Output } else { $null }
}

if ($Json)
{
    $summary | ConvertTo-Json -Depth 8
    exit 0
}

Write-Output $summary.Headline
Write-Output ''
Write-Output ('Daemon:         {0}' -f $summary.DaemonStatus)
Write-Output ('Last poll:      {0} ({1})' -f (Format-RelativeTime $summary.LastPollAt), (Format-AbsoluteTime $summary.LastPollAt))
Write-Output ('Home:           {0}' -f $summary.Home)
if ($summary.WatchedRepos.Count -gt 0)
{
    Write-Output ('Watching:       {0}' -f ($summary.WatchedRepos -join ', '))
}
if (-not [string]::IsNullOrWhiteSpace([string]$summary.LatestDaemonLogDirectory))
{
    Write-Output ('Latest log dir: {0}' -f $summary.LatestDaemonLogDirectory)
}
Write-Output ('Sessions:       running={0}, pending={1}, dispatching={2}, waitingForReview={3}, waitingForFeedback={4}, failed={5}, orphaned={6}, terminal={7}' -f `
    $summary.Counts.Running,
    $summary.Counts.Pending,
    $summary.Counts.Dispatching,
    $summary.Counts.WaitingForReview,
    $summary.Counts.WaitingForFeedback,
    $summary.Counts.Failed,
    $summary.Counts.Orphaned,
    $summary.Counts.Terminal)

if ($summary.RunningSessions.Count -gt 0)
{
    Write-Output ''
    Write-Output 'Current work:'
    foreach ($session in $summary.RunningSessions)
    {
        Write-Output ('- {0} — {1}' -f $session.Subject, $session.Title)
        Write-Output ('  status: running (PID {0}), updated {1}' -f $session.ProcessId, (Format-RelativeTime $session.UpdatedAt))
        if (-not [string]::IsNullOrWhiteSpace([string]$session.LastEvent))
        {
            Write-Output ('  last event: {0} — {1}' -f (Format-AbsoluteTime $session.LastEventAt), $session.LastEvent)
        }
    }
}

if ($summary.PendingSessions.Count -gt 0)
{
    Write-Output ''
    Write-Output 'Queued next:'
    foreach ($session in $summary.PendingSessions)
    {
        Write-Output ('- {0} — {1}' -f $session.Subject, $session.Title)
        Write-Output ('  status: pending, updated {0}' -f (Format-RelativeTime $session.UpdatedAt))
        if (-not [string]::IsNullOrWhiteSpace([string]$session.LastEvent))
        {
            Write-Output ('  last event: {0} — {1}' -f (Format-AbsoluteTime $session.LastEventAt), $session.LastEvent)
        }
    }
}

if ($summary.RecentWaitingSessions.Count -gt 0)
{
    Write-Output ''
    Write-Output ('Recently touched waiting sessions (top {0}):' -f $summary.RecentWaitingSessions.Count)
    foreach ($session in $summary.RecentWaitingSessions)
    {
        Write-Output ('- {0} — {1}' -f $session.Subject, $session.Title)
        Write-Output ('  status: {0}, updated {1}' -f $session.Status, (Format-RelativeTime $session.UpdatedAt))
        if (-not [string]::IsNullOrWhiteSpace([string]$session.LastHookEvent))
        {
            $hookDetail = if ([string]::IsNullOrWhiteSpace([string]$session.LastHookDetail)) { '' } else { " ($($session.LastHookDetail))" }
            Write-Output ('  last hook: {0}{1}' -f $session.LastHookEvent, $hookDetail)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$session.LastEvent))
        {
            Write-Output ('  last event: {0} — {1}' -f (Format-AbsoluteTime $session.LastEventAt), $session.LastEvent)
        }
    }
}

if ($summary.NeedsAttention.Count -gt 0)
{
    Write-Output ''
    Write-Output 'Needs attention:'
    foreach ($session in $summary.NeedsAttention)
    {
        Write-Output ('- {0} — {1}' -f $session.Subject, $session.Status)
        Write-Output ('  updated {0}; retries={1}' -f (Format-RelativeTime $session.UpdatedAt), $session.RetryCount)
        if (-not [string]::IsNullOrWhiteSpace([string]$session.FailureDetail))
        {
            Write-Output ('  failure: {0}' -f $session.FailureDetail)
        }
    }
}

if ($summary.RecentEvents.Count -gt 0)
{
    Write-Output ''
    Write-Output ('Recent notable daemon events (top {0}):' -f $summary.RecentEvents.Count)
    foreach ($event in $summary.RecentEvents)
    {
        Write-Output ('- {0} [{1}] {2}' -f (Format-AbsoluteTime $event.Timestamp), $event.Source, $event.Message)
    }
}

if ($IncludeRawStatus -and $null -ne $summary.RawStatus)
{
    Write-Output ''
    Write-Output 'Raw copilotd status:'
    foreach ($line in $summary.RawStatus)
    {
        Write-Output $line
    }
}
