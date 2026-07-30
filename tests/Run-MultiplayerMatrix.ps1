[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GameRoot,

    [Parameter(Mandatory)]
    [string]$SeedSettingsPath,

    [int[]]$PlayerCounts = @(2, 3, 4),

    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 90,

    [string]$ArtifactRoot =
        (Join-Path $PSScriptRoot '..\artifacts\f7-matrix'),

    [ValidateSet('', 'diagnostics-handler-throw-once')]
    [string]$FaultId = '',

    [switch]$VerifyCollaboration,

    [switch]$KeepSuccessArtifacts
)

$ErrorActionPreference = 'Stop'
$game = [IO.Path]::GetFullPath($GameRoot)
$artifacts = [IO.Path]::GetFullPath($ArtifactRoot)
$seed = [IO.Path]::GetFullPath($SeedSettingsPath)
$exe = Join-Path $game 'SlayTheSpire2.exe'
$guardDll = Join-Path $PSScriptRoot '..\src\CoopGuard\bin\Release\net9.0\CoopGuard.dll'
$driverProject = Join-Path $PSScriptRoot '..\artifacts\test-driver\CoopGuardTestDriver.csproj'

if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or
    !(Test-Path -LiteralPath $seed -PathType Leaf) -or
    !(Test-Path -LiteralPath $driverProject -PathType Leaf)) {
    throw 'GameRoot, seed settings, or tracked test driver is missing.'
}

$live = [IO.Path]::GetFullPath(
    'C:\SteamLibrary\steamapps\common\Slay the Spire 2')
if ([string]::Equals(
        $game.TrimEnd('\'),
        $live.TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'F7 refuses the live Steam game directory; pass an isolated copy.'
}

foreach ($count in $PlayerCounts) {
    if ($count -lt 2 -or $count -gt 4) {
        throw "Unsupported player count: $count"
    }
}

dotnet build (Join-Path $PSScriptRoot '..\src\CoopGuard\CoopGuard.csproj') `
    -c Release "-p:Sts2Path=$game"
if ($LASTEXITCODE -ne 0) {
    throw 'CoopGuard Release build failed.'
}

dotnet build $driverProject -c Debug "-p:Sts2Path=$game"
if ($LASTEXITCODE -ne 0) {
    throw 'Debug-only test driver build failed.'
}

$guardMod = Join-Path $game 'mods\CoopGuard'
$driverMod = Join-Path $game 'mods\CoopGuardTestDriver'
if (!(Test-Path -LiteralPath $guardMod -PathType Container) -or
    !(Test-Path -LiteralPath $driverMod -PathType Container)) {
    throw 'The isolated game copy must contain CoopGuard and CoopGuardTestDriver Mod folders.'
}

Copy-Item -LiteralPath $guardDll -Destination (
    Join-Path $guardMod 'CoopGuard.dll') -Force
$driverOutput = Join-Path $PSScriptRoot '..\artifacts\test-driver\bin\Debug\net9.0'
foreach ($name in @(
        'CoopGuardTestDriver.dll',
        'CoopGuardTestDriver.json')) {
    Copy-Item -LiteralPath (Join-Path $driverOutput $name) `
        -Destination (Join-Path $driverMod $name) -Force
}
Copy-Item -LiteralPath (
    Join-Path $PSScriptRoot '..\artifacts\test-driver\coopguard.settings.json') `
    -Destination (Join-Path $driverMod 'coopguard.settings.json') -Force

[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$summary = [Collections.Generic.List[object]]::new()
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')

function Stop-ExactProcesses {
    param([Collections.Generic.List[Diagnostics.Process]]$Processes)

    foreach ($process in $Processes) {
        try {
            if (!$process.HasExited) {
                $process.Kill($true)
            }
        }
        catch {
            # A process that exited between checks already satisfies cleanup.
        }
    }
    foreach ($process in $Processes) {
        try {
            $process.WaitForExit(5000) | Out-Null
        }
        catch {
        }
    }
}

function Read-BoundedLog {
    param([string]$Path)

    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ''
    }
    $file = [IO.FileInfo]::new($Path)
    if ($file.Length -gt 16MB) {
        throw "Log exceeded 16 MiB: $($file.Name)"
    }
    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $reader = [IO.StreamReader]::new($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

foreach ($players in $PlayerCounts) {
    $scenario = Join-Path $artifacts "CG-F7-$stamp-${players}p"
    $resolvedParent = [IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'
    $resolvedScenario = [IO.Path]::GetFullPath($scenario)
    if (!$resolvedScenario.StartsWith(
            $resolvedParent,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Scenario path escaped ArtifactRoot.'
    }

    [IO.Directory]::CreateDirectory($scenario) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $scenario '.coopguard-isolated-test'),
        'CoopGuard F7/F8 isolated profile marker.')
    $port = 34770 + $players
    $processes =
        [Collections.Generic.List[Diagnostics.Process]]::new()
    $outputPaths = [Collections.Generic.List[string]]::new()
    $errorPaths = [Collections.Generic.List[string]]::new()
    $roles = [Collections.Generic.List[string]]::new()
    $passed = $false
    $failure = ''

    try {
        for ($index = 0; $index -lt $players; $index++) {
            $clientId = if ($index -eq 0) { 1 } else { 999 + $index }
            $role = if ($index -eq 0) { 'host' } else { "client-$index" }
            $profile = Join-Path $scenario $role
            $roaming = Join-Path $profile 'Roaming'
            $local = Join-Path $profile 'Local'
            $temp = Join-Path $profile 'Temp'
            $settingsDir = Join-Path $roaming (
                "SlayTheSpire2\default\$clientId")
            [IO.Directory]::CreateDirectory($settingsDir) | Out-Null
            [IO.Directory]::CreateDirectory($local) | Out-Null
            [IO.Directory]::CreateDirectory($temp) | Out-Null
            Copy-Item -LiteralPath $seed -Destination (
                Join-Path $settingsDir 'settings.save') -Force

            $arguments = @(
                '--force-steam=off',
                $(if ($index -eq 0) {
                    '--fastmp=host_standard'
                } else {
                    '--fastmp=join'
                }),
                "--clientId=$clientId",
                "--cgtest-players=$players",
                '--cgtest-protocol=1',
                '--cgtest-settings=1',
                "--cgtest-port=$port")
            if ($VerifyCollaboration) {
                $arguments += '--cgtest-collaboration=1'
            }
            if ($FaultId -ne '' -and $index -eq 1) {
                $arguments += "--cgtest-fault=$FaultId"
                $arguments += "`"--cgtest-isolated-root=$scenario`""
            }
            $stdoutPath = Join-Path $scenario "$role.stdout.log"
            $stderrPath = Join-Path $scenario "$role.stderr.log"
            $oldAppData = $env:APPDATA
            $oldLocalAppData = $env:LOCALAPPDATA
            $oldTemp = $env:TEMP
            $oldTmp = $env:TMP
            $oldDisplay = $env:GODOT_DISPLAY_DRIVER
            try {
                $env:APPDATA = $roaming
                $env:LOCALAPPDATA = $local
                $env:TEMP = $temp
                $env:TMP = $temp
                $env:GODOT_DISPLAY_DRIVER = 'headless'
                $process = Start-Process `
                    -FilePath $exe `
                    -ArgumentList $arguments `
                    -WorkingDirectory $game `
                    -RedirectStandardOutput $stdoutPath `
                    -RedirectStandardError $stderrPath `
                    -WindowStyle Hidden `
                    -PassThru
            }
            finally {
                $env:APPDATA = $oldAppData
                $env:LOCALAPPDATA = $oldLocalAppData
                $env:TEMP = $oldTemp
                $env:TMP = $oldTmp
                $env:GODOT_DISPLAY_DRIVER = $oldDisplay
            }
            $processes.Add($process)
            $outputPaths.Add($stdoutPath)
            $errorPaths.Add($stderrPath)
            $roles.Add($role)
            if ($index -eq 0) {
                Start-Sleep -Milliseconds 800
            }
        }

        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $allReady = $true
            for ($index = 0; $index -lt $processes.Count; $index++) {
                $text = Read-BoundedLog $outputPaths[$index]
                if ($text -match
                    'TOOLKIT_PROTOCOL_FAILED|Unhandled exception|\[FATAL\]') {
                    throw "Failure sentinel from $($roles[$index])."
                }
                if ($processes[$index].HasExited -and
                    $processes[$index].ExitCode -ne 0) {
                    throw "$($roles[$index]) exited with $($processes[$index].ExitCode)."
                }
                if (!$text.Contains(
                        "TOOLKIT_PROTOCOL_OK",
                        [StringComparison]::Ordinal) -or
                    !$text.Contains(
                        "matrixRows=$players",
                        [StringComparison]::Ordinal) -or
                    !$text.Contains(
                        'Embarking on a multiplayer run',
                        [StringComparison]::Ordinal) -or
                    ($VerifyCollaboration -and
                        !$text.Contains(
                            'TOOLKIT_COLLABORATION_OK unanimous=1 contributionSharing=1',
                            [StringComparison]::Ordinal))) {
                    $allReady = $false
                }
            }
            if ($allReady) {
                $passed = $true
                break
            }
            Start-Sleep -Milliseconds 250
        }

        if (!$passed) {
            throw "Scenario ${players}p timed out after $TimeoutSeconds seconds."
        }

        $tokens = @(
            foreach ($outputPath in $outputPaths) {
                $match = [regex]::Match(
                    (Read-BoundedLog $outputPath),
                    'TOOLKIT_PROTOCOL_OK sessionToken=([0-9A-F]{16})')
                if ($match.Success) {
                    $match.Groups[1].Value
                }
            })
        if ($tokens.Count -ne $players -or
            @($tokens | Select-Object -Unique).Count -ne 1) {
            throw 'Peers did not prove one shared diagnostics session.'
        }
        if ($FaultId -ne '') {
            $faultCount = (
                (Read-BoundedLog $outputPaths[1]).Split(
                    'F8_FAULT_INJECTED',
                    [StringSplitOptions]::None).Count - 1)
            if ($faultCount -ne 1) {
                throw "F8 fault count was $faultCount, expected exactly one."
            }
        }
    }
    catch {
        $passed = $false
        $failure = $_.Exception.Message
    }
    finally {
        Stop-ExactProcesses -Processes $processes
    }

    $summary.Add([ordered]@{
        players = $players
        port = $port
        passed = $passed
        fault = if ($FaultId -eq '') { 'none' } else { $FaultId }
        failure = $failure
        artifacts = if ($passed -and !$KeepSuccessArtifacts) {
            'cleaned'
        } else {
            'retained-local'
        }
    })

    if (!$passed) {
        [IO.File]::WriteAllText(
            (Join-Path $scenario 'FAILURE.txt'),
            $failure)
        break
    }

    if (!$KeepSuccessArtifacts) {
        Remove-Item -LiteralPath $resolvedScenario -Recurse -Force
    }
}

$summaryPath = Join-Path $artifacts "summary-$stamp.json"
[IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 4))
if (@($summary | Where-Object { -not $_.passed }).Count -ne 0) {
    throw "F7 matrix failed; local artifacts retained. Summary: $summaryPath"
}

Write-Output "F7 matrix passed for $($PlayerCounts -join '/') peers. Summary: $summaryPath"
