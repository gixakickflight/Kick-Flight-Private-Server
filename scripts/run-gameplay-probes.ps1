[CmdletBinding()]
param(
    [string[]]$Serials = @('emulator-5554', 'emulator-5556'),
    [string]$ActorSerial = 'emulator-5554',
    [string]$ObserverSerial = 'emulator-5556',
    [string]$ApkPath,
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [string]$EvidenceRoot = '.local\gameplay-evidence\automated',
    [int]$ObserveSeconds = 195,
    [int]$LaunchStaggerSeconds = 45,
    [bool]$RestartEmulators = $true,
    [bool]$VisibleEmulators = $true,
    [bool]$ResetAppData = $true,
    [bool]$SeedAssetCache = $true,
    [bool]$InstallApk = $false,
    [switch]$FlightDiagnostic,
    [switch]$DiscImpactDiagnostic,
    [int]$ExpectedBattleRuleId = 0,
    [switch]$SkipUltimate,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$adb = Join-Path $repo '.local\android-sdk\platform-tools\adb.exe'
$battleEntryRunner = Join-Path $PSScriptRoot 'run-battle-entry.ps1'
$runDirectory = [IO.Path]::GetFullPath((Join-Path $repo (Join-Path $EvidenceRoot $RunId)))
$entryRunId = "$RunId-entry"
$entryDirectory = [IO.Path]::GetFullPath((Join-Path $repo (Join-Path $EvidenceRoot $entryRunId)))
$transcriptPath = Join-Path $runDirectory 'probe-commands.jsonl'
$summaryPath = Join-Path $runDirectory 'probe-summary.json'
$package = 'world.global.fenix'
$kickerParameterPath = Join-Path $repo 'config\masters_kicker_parameter.json'
$phase = 'initialize'
$startedAt = Get-Date

function Write-JsonLine([hashtable]$Record) {
    $Record.timestamp = (Get-Date).ToString('o')
    Add-Content -LiteralPath $transcriptPath -Value ($Record | ConvertTo-Json -Compress -Depth 6)
}

function Invoke-Adb {
    param(
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure,
        [switch]$CompactTranscript
    )

    $output = @(& $adb -s $Serial @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $fullOutput = $output -join "`n"
    $recordedOutput = $fullOutput
    if ($CompactTranscript -and $recordedOutput.Length -gt 1600) {
        $recordedOutput = "[truncated to final 1600 chars; total=$($recordedOutput.Length)]`n" +
            $recordedOutput.Substring($recordedOutput.Length - 1600)
    }
    Write-JsonLine @{
        kind = 'adb'
        phase = $script:phase
        serial = $Serial
        arguments = $Arguments
        exitCode = $exitCode
        output = $recordedOutput
    }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "ADB failed for $Serial during $script:phase (exit $exitCode): $($Arguments -join ' ')"
    }
    return $fullOutput
}

function Get-AppPid([string]$Serial) {
    return (Invoke-Adb -Serial $Serial -Arguments @('shell', 'pidof', $package) -AllowFailure -CompactTranscript).Trim()
}

function Assert-AppsAlive([string]$Context) {
    foreach ($serial in $Serials) {
        if (-not (Get-AppPid $serial)) {
            throw "$serial app process died during $Context."
        }
    }
}

function Save-Screenshot([string]$Serial, [string]$Name) {
    $remote = "/data/local/tmp/kf-$RunId-$Name.png"
    $raw = Join-Path $runDirectory "$Name-$Serial-raw.png"
    $scaled = Join-Path $runDirectory "$Name-$Serial-720.png"
    Invoke-Adb -Serial $Serial -Arguments @('shell', 'screencap', '-p', $remote) | Out-Null
    Invoke-Adb -Serial $Serial -Arguments @('pull', $remote, $raw) | Out-Null
    Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null

    Add-Type -AssemblyName System.Drawing
    $source = [Drawing.Image]::FromFile($raw)
    try {
        $height = [Math]::Round($source.Height * (720.0 / $source.Width))
        $bitmap = New-Object Drawing.Bitmap 720, $height
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($source, 0, 0, 720, $height)
            } finally {
                $graphics.Dispose()
            }
            $bitmap.Save($scaled, [Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $bitmap.Dispose()
        }
    } finally {
        $source.Dispose()
    }
    Remove-Item -LiteralPath $raw -Force
    Write-JsonLine @{ kind = 'screenshot'; phase = $phase; serial = $Serial; path = $scaled }
    return $scaled
}

function Save-Pair([string]$Name) {
    foreach ($serial in $Serials) {
        Save-Screenshot -Serial $serial -Name $Name | Out-Null
    }
}

function Wait-AndCapture-ResultScene([int]$TimeoutSeconds = 30) {
    $seen = @{}
    foreach ($serial in $Serials) { $seen[$serial] = $false }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $sample = 0
    while ((Get-Date) -lt $deadline -and ($seen.Values -contains $false)) {
        foreach ($serial in $Serials) {
            if ($seen[$serial]) { continue }
            $lines = @(& $adb -s $serial logcat -d -v threadtime 2>&1)
            if ($lines -match '\bResultScene\b') {
                $seen[$serial] = $true
                $phase = 'result-scene'
                Save-Screenshot -Serial $serial -Name ("result-scene-{0:D2}-{1}" -f $sample, $serial) | Out-Null
                Write-JsonLine @{ kind = 'checkpoint'; serial = $serial; phase = $phase; marker = 'ResultScene'; sample = $sample }
            }
        }
        if ($seen.Values -contains $false) {
            $sample++
            Start-Sleep -Seconds 2
        }
    }
    if ($seen.Values -contains $true) { $milestones.Add('result-scene') }
    if ($seen.Values -contains $false) {
        Write-JsonLine @{ kind = 'checkpoint'; phase = 'result-scene'; marker = 'ResultScene-timeout'; seen = $seen }
    }
}

function Save-AudioSnapshot([string]$Name) {
    foreach ($serial in $Serials) {
        $audio = Invoke-Adb -Serial $serial -Arguments @('shell', 'dumpsys', 'media.audio_flinger') -AllowFailure -CompactTranscript
        $path = Join-Path $runDirectory "audio-$Name-$serial.txt"
        Set-Content -LiteralPath $path -Value $audio
        Write-JsonLine @{ kind = 'audio-snapshot'; phase = $phase; serial = $serial; name = $Name; path = $path }
    }
}

function Write-SpeedProbeMarker {
    if (-not (Test-Path -LiteralPath $kickerParameterPath -PathType Leaf)) { return }
    $parameters = @(Get-Content -Raw -LiteralPath $kickerParameterPath | ConvertFrom-Json)
    if ($parameters.Count -eq 0) { return }
    $sample = $parameters[0]
    $message = 'configured speed={0} dashCoefficient={1} acceleration={2}' -f $sample.speed, $sample.moveDashSpeedCoefficient, $sample.acceleration
    foreach ($serial in $Serials) {
        Invoke-Adb -Serial $serial -Arguments @('shell', 'log', '-t', 'KFPROBE_SPEED', $message) -AllowFailure | Out-Null
    }
    Write-JsonLine @{
        kind = 'speed-configuration'
        phase = $phase
        speed = $sample.speed
        dashCoefficient = $sample.moveDashSpeedCoefficient
        acceleration = $sample.acceleration
    }
}

function Get-BattleVisualState([string]$Serial) {
    $remote = "/data/local/tmp/kf-$RunId-battle-probe.png"
    $probe = Join-Path $runDirectory ".battle-probe-$Serial.png"
    try {
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'screencap', '-p', $remote) | Out-Null
        Invoke-Adb -Serial $Serial -Arguments @('pull', $remote, $probe) | Out-Null
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
        Add-Type -AssemblyName System.Drawing
        $image = [Drawing.Bitmap]::FromFile($probe)
        try {
            if ($image.Width -ne 1080 -or $image.Height -ne 1920) { return 'unknown' }
            $greenSamples = 0
            for ($x = 600; $x -le 1050; $x += 15) {
                for ($y = 1800; $y -le 1890; $y += 15) {
                    $pixel = $image.GetPixel($x, $y)
                    if ($pixel.G -ge 150 -and $pixel.G -gt ($pixel.R + 30) -and $pixel.G -gt ($pixel.B + 30)) {
                        $greenSamples++
                    }
                }
            }
            if ($greenSamples -ge 20) { return 'hud-ready' }
            return 'loading'
        } finally {
            $image.Dispose()
        }
    } finally {
        if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force }
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
    }
}

function Wait-AllBattleHud([int]$TimeoutSeconds) {
    $pending = @{}
    foreach ($serial in $Serials) { $pending[$serial] = $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($serial in @($pending.Keys)) {
            if (-not (Get-AppPid $serial)) { throw "$serial app process died while the battle HUD was rendering." }
            if ((Get-BattleVisualState -Serial $serial) -eq 'hud-ready') {
                $pending.Remove($serial)
                Write-JsonLine @{ kind = 'checkpoint'; serial = $serial; phase = $phase; marker = 'battle-hud-visible' }
            }
        }
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Seconds 3
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for battle HUD on: $($pending.Keys -join ', ')."
}

function Save-Logcats {
    foreach ($serial in $Serials) {
        $lines = @(& $adb -s $serial logcat -d -v threadtime 2>&1)
        $exitCode = $LASTEXITCODE
        $full = $lines -join "`n"
        Write-JsonLine @{
            kind = 'adb-logcat-dump'
            phase = $phase
            serial = $serial
            exitCode = $exitCode
            lineCount = $lines.Count
            characterCount = $full.Length
        }
        Set-Content -LiteralPath (Join-Path $runDirectory "logcat-$serial-full.txt") -Value $full
        $filtered = $lines | Select-String -Pattern (
            'Unity|GameScene|MatchingScene|CriAtom|ADX|AudioTrack|AudioFlinger|BGM|battle0[1-5]|' +
            'DiscPresenter|OnFlickUp|SkillRangeValidator|DiscSkillParameter|SendUseDisc|' +
            'SpecialSkill|PlayerStateSpecialSkill|SkillAction|KickerSkillAction|ObjectManager|' +
            'PlayerRPC|UpdateFlySpeed|MoveSpeed|Acceleration|Dash|Photon|SetProperties|RaiseEvent|' +
            'SIGSEGV|signal 11|FATAL|AndroidRuntime|NullReference|Exception|disconnect'
        ) -CaseSensitive:$false
        Set-Content -LiteralPath (Join-Path $runDirectory "logcat-$serial-filtered.txt") -Value $filtered
    }
}

function Save-LuxonLog {
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if (-not $docker) {
        Write-JsonLine @{ kind = 'diagnostic-skip'; phase = $phase; diagnostic = 'luxon-log'; reason = 'docker-not-found' }
        return
    }
    $since = $startedAt.ToUniversalTime().ToString('o')
    $lines = @(& $docker.Source logs --since $since luxon-server 2>&1)
    $exitCode = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $runDirectory 'luxon-server.log') -Value $lines
    Write-JsonLine @{
        kind = 'luxon-log-dump'
        phase = $phase
        since = $since
        exitCode = $exitCode
        lineCount = $lines.Count
    }
}

function Invoke-Swipe {
    param(
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)][int]$X1,
        [Parameter(Mandatory)][int]$Y1,
        [Parameter(Mandatory)][int]$X2,
        [Parameter(Mandatory)][int]$Y2,
        [Parameter(Mandatory)][int]$DurationMs,
        [Parameter(Mandatory)][string]$Action
    )
    $script:phase = $Action
    Invoke-Adb -Serial $Serial -Arguments @(
        'shell', 'input', 'swipe', "$X1", "$Y1", "$X2", "$Y2", "$DurationMs"
    ) | Out-Null
    Write-JsonLine @{
        kind = 'gesture'
        phase = $phase
        serial = $Serial
        action = $Action
        from = @($X1, $Y1)
        to = @($X2, $Y2)
        durationMs = $DurationMs
    }
}

New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

if (-not (Test-Path -LiteralPath $adb -PathType Leaf)) { throw "ADB not found: $adb" }
if (-not (Test-Path -LiteralPath $battleEntryRunner -PathType Leaf)) { throw "Runner not found: $battleEntryRunner" }
if ($Serials.Count -ne 2 -or ($Serials | Select-Object -Unique).Count -ne 2) {
    throw 'Exactly two distinct device serials are required.'
}
if ($ActorSerial -notin $Serials -or $ObserverSerial -notin $Serials -or $ActorSerial -eq $ObserverSerial) {
    throw 'ActorSerial and ObserverSerial must be distinct members of Serials.'
}
if ($ObserveSeconds -lt 30) { throw 'ObserveSeconds must be at least 30.' }
if ($ExpectedBattleRuleId -lt 0) { throw 'ExpectedBattleRuleId must be nonnegative (0 disables the check).' }

function Assert-ExpectedBattleRule {
    if ($ExpectedBattleRuleId -eq 0) { return }
    $logPath = Join-Path $entryDirectory 'server-stdout.txt'
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw 'Cannot verify battle rule: entry backend log is missing.'
    }
    $entries = @(foreach ($line in [IO.File]::ReadLines($logPath)) {
        try { $record = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($record.Category -eq 'KickFlight.BootstrapApi.BattleMatchmakingService' -and
            $record.State.UserId -and $null -ne $record.State.RuleId -and
            $record.Message -like 'Registered battle entry:*') {
            $record.State
        }
    })
    $users = @($entries | ForEach-Object { $_.UserId } | Select-Object -Unique)
    if ($users.Count -ne $Serials.Count -or @($entries | Where-Object { $_.RuleId -ne $ExpectedBattleRuleId }).Count) {
        throw "Battle rule is unverified or differs from required rule $ExpectedBattleRuleId; no gameplay inputs will run. Inspect $logPath"
    }
    Write-JsonLine @{ kind = 'battle-rule-confirmed'; ruleId = $ExpectedBattleRuleId; users = $users }
}

if ($ValidateOnly) {
    Write-JsonLine @{
        kind = 'validation'
        result = 'ok'
        serials = $Serials
        actor = $ActorSerial
        observer = $ObserverSerial
    }
    Write-Host "Gameplay probe validation passed. Evidence directory: $runDirectory"
    exit 0
}

$result = 'failed'
$failure = $null
$milestones = [Collections.Generic.List[string]]::new()
try {
    $phase = 'battle-entry'
    $entryArguments = @{
        Serials = $Serials
        RunId = $entryRunId
        Target = 'GameScene'
        GameSettleSeconds = 8
        LaunchStaggerSeconds = $LaunchStaggerSeconds
        ResetAppData = $ResetAppData
        RestartEmulators = $RestartEmulators
        SeedAssetCache = $SeedAssetCache
        VisibleEmulators = $VisibleEmulators
    }
    if ($InstallApk) {
        if (-not $ApkPath) { throw 'Provide -ApkPath when -InstallApk is enabled.' }
        $entryArguments.ApkPath = $ApkPath
    } else {
        $entryArguments.SkipInstall = $true
    }
    & $battleEntryRunner @entryArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Battle-entry runner failed. Inspect $entryDirectory"
    }
    $milestones.Add('battle-entry')
    $phase = 'verify-battle-rule'
    Assert-ExpectedBattleRule
    Assert-AppsAlive 'post-entry preflight'

    $phase = 'wait-battle-hud'
    Wait-AllBattleHud -TimeoutSeconds 120
    $milestones.Add('battle-hud')

    $phase = 'baseline'
    Save-Pair '00-baseline'
    Save-AudioSnapshot '000s'
    Write-SpeedProbeMarker
    $milestones.Add('baseline')

    if ($FlightDiagnostic) {
        # User-confirmed control: tap an empty screen point to advance.
        $phase = 'flight-start-tap'
        foreach ($serial in $Serials) {
            Invoke-Adb -Serial $serial -Arguments @('shell', 'input', 'tap', '540', '1200') | Out-Null
        }
        Start-Sleep -Seconds 1
        Save-Pair 'flight-01-after-tap'
        Start-Sleep -Seconds 5
        Assert-AppsAlive 'flight diagnostic'
        Save-Pair 'flight-02-after-tap'
        $milestones.Add('flight-inputs-attempted')
        $result = 'passed'
        return
    }

    if ($DiscImpactDiagnostic) {
        # auto70's synchronized positions show the two players within 40 units
        # about 3-4 seconds after the tap. Activate verified Geckosaurus slot2
        # during that crossing, before paired screenshots consume the window.
        $phase = 'disc-impact-approach'
        foreach ($serial in $Serials) {
            Invoke-Adb -Serial $serial -Arguments @('shell', 'input', 'tap', '540', '1200') | Out-Null
        }
        Start-Sleep -Milliseconds 2700
        foreach ($serial in $Serials) {
            Invoke-Swipe -Serial $serial -X1 310 -Y1 1740 -X2 310 -Y2 1380 -DurationMs 400 -Action 'disc-impact-slot-2'
        }
        Save-Pair 'disc-impact-01-after-slot-2'
        Start-Sleep -Seconds 2
        Assert-AppsAlive 'disc impact diagnostic'
        Save-Pair 'disc-impact-02-observed'
        $milestones.Add('disc-impact-inputs-attempted')
        $result = 'passed'
        return
    }

    # A slow, short drag keeps steering input active long enough to distinguish
    # ordinary flight from the quick flick used for dash.
    Invoke-Swipe -Serial $ActorSerial -X1 540 -Y1 1200 -X2 540 -Y2 900 -DurationMs 600 -Action 'normal-flight'
    Start-Sleep -Milliseconds 700
    Assert-AppsAlive 'normal flight'
    Save-Pair '01-after-normal-flight'
    $milestones.Add('normal-flight')

    # Same direction, deliberately faster and longer: expected to trigger the
    # client's dash threshold rather than another steering drag.
    Invoke-Swipe -Serial $ActorSerial -X1 540 -Y1 1200 -X2 540 -Y2 450 -DurationMs 300 -Action 'dash'
    Start-Sleep -Milliseconds 900
    Assert-AppsAlive 'dash'
    Save-Pair '02-after-dash'
    $milestones.Add('dash')

    # Move both human clients toward the center while they are still under
    # automation control. The paired capture is dedicated evidence for the
    # strict sync criterion: the remote human should change position on the
    # radar and, when line-of-sight permits, appear in the other 3D view.
    Invoke-Swipe -Serial $ActorSerial -X1 540 -Y1 1200 -X2 540 -Y2 720 -DurationMs 550 -Action 'sync-actor-approach'
    Invoke-Swipe -Serial $ObserverSerial -X1 540 -Y1 1200 -X2 540 -Y2 720 -DurationMs 550 -Action 'sync-observer-approach'
    Start-Sleep -Seconds 3
    Assert-AppsAlive 'sync rendezvous'
    $phase = 'sync-rendezvous'
    Save-Pair '02b-sync-rendezvous'
    $milestones.Add('sync-rendezvous')

    # Slot 1 center is derived from the 1080x1920 battle HUD. Flick upward to
    # activate it while avoiding the adjacent slot and the Android gesture bar.
    Invoke-Swipe -Serial $ActorSerial -X1 110 -Y1 1740 -X2 110 -Y2 1380 -DurationMs 400 -Action 'disc-slot-1'
    Start-Sleep -Seconds 2
    Assert-AppsAlive 'disc slot 1'
    Save-Pair '03-after-disc-slot-1'
    $milestones.Add('disc-slot-1')

    $observationStart = Get-Date
    $checkpoints = @(30, 90, 120, 180) | Where-Object { $_ -le $ObserveSeconds }
    foreach ($checkpoint in $checkpoints) {
        $remaining = $checkpoint - [int]((Get-Date) - $observationStart).TotalSeconds
        while ($remaining -gt 0) {
            Start-Sleep -Seconds ([Math]::Min(5, $remaining))
            Assert-AppsAlive "observation checkpoint ${checkpoint}s"
            $remaining = $checkpoint - [int]((Get-Date) - $observationStart).TotalSeconds
        }

        if ($checkpoint -eq 120 -and -not $SkipUltimate) {
            $phase = 'ultimate-before'
            Save-Pair '04-before-ultimate'
            Save-AudioSnapshot '120s-before-ultimate'
            # The kicker-special gauge is the circular control centered above
            # the stats bar. If it is still disabled, this tap is inert and the
            # before/after images make that explicit.
            $phase = 'ultimate-tap'
            Invoke-Adb -Serial $ActorSerial -Arguments @('shell', 'input', 'tap', '540', '1695') | Out-Null
            Write-JsonLine @{ kind = 'gesture'; phase = $phase; serial = $ActorSerial; action = 'ultimate'; at = @(540, 1695) }
            Start-Sleep -Seconds 3
            Assert-AppsAlive 'ultimate attempt'
            Save-Pair '05-after-ultimate'
            # The cut-in occupies roughly the first three seconds. Capture the
            # active window twice so a ten-second buff/effect is not sampled
            # only after it has already expired.
            Start-Sleep -Seconds 3
            Assert-AppsAlive 'ultimate post-cinematic effect'
            Save-Pair '06-ultimate-effect'
            Save-AudioSnapshot '126s-after-ultimate'
            Start-Sleep -Seconds 3
            Assert-AppsAlive 'ultimate late effect'
            Save-Pair '07-ultimate-late-effect'
            Save-AudioSnapshot '129s-after-ultimate'
            $milestones.Add('ultimate-attempt')
        } else {
            $phase = "observe-${checkpoint}s"
            Save-Pair ("observe-{0:D3}s" -f $checkpoint)
            Save-AudioSnapshot ("{0:D3}s" -f $checkpoint)
        }
    }

    $phase = 'final-observation'
    $remaining = $ObserveSeconds - [int]((Get-Date) - $observationStart).TotalSeconds
    while ($remaining -gt 0) {
        Start-Sleep -Seconds ([Math]::Min(5, $remaining))
        Assert-AppsAlive 'final observation'
        $remaining = $ObserveSeconds - [int]((Get-Date) - $observationStart).TotalSeconds
    }
    Save-Pair '99-final'
    $milestones.Add('full-observation')
    Wait-AndCapture-ResultScene -TimeoutSeconds 30
    $result = 'passed'
} catch {
    $failure = $_.Exception.Message
    try { Save-Pair "failure-$phase" } catch {}
} finally {
    try { Save-Logcats } catch {
        Write-JsonLine @{ kind = 'diagnostic-error'; phase = $phase; message = $_.Exception.Message }
    }
    try { Save-LuxonLog } catch {
        Write-JsonLine @{ kind = 'diagnostic-error'; phase = $phase; diagnostic = 'luxon-log'; message = $_.Exception.Message }
    }
    $devices = @{}
    foreach ($serial in $Serials) {
        $devices[$serial] = @{
            pid = (Get-AppPid $serial)
            state = (& $adb -s $serial get-state 2>$null)
        }
    }
    @{
        runId = $RunId
        entryRunId = $entryRunId
        result = $result
        finalPhase = $phase
        failure = $failure
        actor = $ActorSerial
        observer = $ObserverSerial
        observeSeconds = $ObserveSeconds
        flightDiagnostic = [bool]$FlightDiagnostic
        discImpactDiagnostic = [bool]$DiscImpactDiagnostic
        expectedBattleRuleId = $ExpectedBattleRuleId
        milestones = $milestones
        devices = $devices
        evidenceDirectory = $runDirectory
        entryEvidenceDirectory = $entryDirectory
        startedAt = $startedAt.ToString('o')
        completedAt = (Get-Date).ToString('o')
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath
}

if ($result -ne 'passed') {
    Write-Error "Gameplay probes failed during '$phase': $failure. Evidence: $runDirectory"
    exit 1
}
Write-Host "Gameplay probes completed. Evidence: $runDirectory"
