[CmdletBinding()]
param(
    [ValidateSet('InactiveAccount', 'OtherHost', 'InvalidActive', 'NetworkFailure', 'UnsupportedCli')]
    [string]$Scenario
)

$ErrorActionPreference = 'Stop'
$installerPath = Join-Path $PSScriptRoot 'install\install-copilotd.ps1'

if ($Scenario)
{
    # Shadow gh in the child process: no real credentials or GitHub requests are used.
    function gh
    {
        if ($args[0] -eq 'auth')
        {
            $global:LASTEXITCODE = 1
            if (($args -join ' ') -ne 'auth status --active --hostname github.com')
            {
                Write-Error "Stale credential from $Scenario was checked." -ErrorAction Continue
                return
            }

            switch ($Scenario)
            {
                { $_ -in 'InactiveAccount', 'OtherHost' }
                {
                    Write-Output 'Active github.com account is valid.'
                    $global:LASTEXITCODE = 0
                }
                'InvalidActive'
                {
                    Write-Output 'github.com active account'
                    Write-Error 'The active token is invalid (HTTP 401).' -ErrorAction Continue
                }
                'NetworkFailure'
                {
                    Write-Output 'github.com connection attempt'
                    Write-Error 'dial tcp: network is unreachable' -ErrorAction Continue
                }
                'UnsupportedCli'
                {
                    Write-Error 'unknown flag: --active' -ErrorAction Continue
                }
            }
            return
        }

        if ($args[0] -eq 'api')
        {
            Write-Error 'AUTH_PREFLIGHT_PASSED_API_BOUNDARY' -ErrorAction Continue
            $global:LASTEXITCODE = 1
            return
        }

        throw "Unexpected gh invocation: $args"
    }

    & $installerPath -Force -UpdatePath $false
    throw 'Installer unexpectedly completed past the mocked API boundary.'
}

$enginePath = (Get-Process -Id $PID).Path
$cases = @(
    @{ Name = 'InactiveAccount'; Expected = 'AUTH_PREFLIGHT_PASSED_API_BOUNDARY' }
    @{ Name = 'OtherHost'; Expected = 'AUTH_PREFLIGHT_PASSED_API_BOUNDARY' }
    @{ Name = 'InvalidActive'; Expected = 'The active token is invalid (HTTP 401).'; Stdout = 'github.com active account' }
    @{ Name = 'NetworkFailure'; Expected = 'dial tcp: network is unreachable'; Stdout = 'github.com connection attempt' }
    @{ Name = 'UnsupportedCli'; Expected = 'unknown flag: --active' }
)

foreach ($case in $cases)
{
    $previousEAP = $ErrorActionPreference
    try
    {
        $ErrorActionPreference = 'Continue'
        $output = @(& $enginePath -NoProfile -File $PSCommandPath -Scenario $case.Name 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        $ErrorActionPreference = $previousEAP
    }
    $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    if ($exitCode -eq 0 -or -not $text.Contains($case.Expected))
    {
        throw "Scenario '$($case.Name)' failed (exit $exitCode):`n$text"
    }

    if ($case.Name -in 'InactiveAccount', 'OtherHost')
    {
        if ($text.Contains('authentication check failed') -or $text.Contains('Active github.com account is valid.'))
        {
            throw "Scenario '$($case.Name)' did not silently pass preflight:`n$text"
        }
    }
    else
    {
        if ($text.Contains('AUTH_PREFLIGHT_PASSED_API_BOUNDARY') -or
            $text.Contains('is not authenticated') -or
            -not $text.Contains('authentication check failed for the active account on github.com') -or
            -not $text.Contains('gh auth login --hostname github.com') -or
            ($case.Stdout -and -not $text.Contains($case.Stdout)))
        {
            throw "Scenario '$($case.Name)' lost diagnostics or did not stop at preflight:`n$text"
        }
    }

    Write-Host "Scenario '$($case.Name)' passed."
}
