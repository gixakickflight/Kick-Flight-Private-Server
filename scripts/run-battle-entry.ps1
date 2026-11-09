[CmdletBinding()]
param(
    [string[]]$Serials = @('emulator-5554', 'emulator-5556'),
    [string]$ApkPath,
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [ValidateSet('MatchingScene', 'GameScene')]
    [string]$Target = 'GameScene',
    [int]$GameSettleSeconds = 90,
    [int]$LaunchStaggerSeconds = 45,
    [string]$BackendReadyUrl = 'http://127.0.0.1:18080/health/ready',
    [string]$EvidenceRoot = '.local\gameplay-evidence\automated',
    [switch]$SkipInstall,
    [switch]$ResetAppData,
    [switch]$SeedAssetCache,
    [switch]$RestartEmulators,
    [switch]$VisibleEmulators,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$adb = Join-Path $repo '.local\android-sdk\platform-tools\adb.exe'
$package = 'world.global.fenix'
$activity = 'world.global.fenix/com.google.firebase.MessagingUnityPlayerActivity'
$runDirectory = [IO.Path]::GetFullPath((Join-Path $repo (Join-Path $EvidenceRoot $RunId)))
$transcriptPath = Join-Path $runDirectory 'commands.jsonl'
$summaryPath = Join-Path $runDirectory 'summary.json'
$phase = 'initialize'
$sceneMonitors = @{}
$serverLogSources = @()

$timeouts = @{
    Boot = 45
    TitleScene = 180
    HomeScene = 420
    MatchingScene = 150
    GameScene = 150
}

function Write-JsonLine([hashtable]$Record) {
    $Record.timestamp = (Get-Date).ToString('o')
    Add-Content -LiteralPath $transcriptPath -Value ($Record | ConvertTo-Json -Compress -Depth 6)
}

function Save-ServerLogSlices {
    foreach ($source in $script:serverLogSources) {
        if (-not (Test-Path -LiteralPath $source.path -PathType Leaf)) { continue }
        $stream = [IO.File]::Open($source.path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            if ($source.startLength -le $stream.Length) {
                $stream.Seek($source.startLength, [IO.SeekOrigin]::Begin) | Out-Null
            }
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
            try {
                Set-Content -LiteralPath (Join-Path $runDirectory $source.outputName) -Value $reader.ReadToEnd()
            } finally {
                $reader.Dispose()
            }
        } finally {
            $stream.Dispose()
        }
    }
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
    $recordedOutput = ($output -join "`n")
    if ($CompactTranscript -and $recordedOutput.Length -gt 1200) {
        $recordedOutput = "[truncated to final 1200 chars; total=$($recordedOutput.Length)]`n" + $recordedOutput.Substring($recordedOutput.Length - 1200)
    }
    Write-JsonLine @{
        kind = 'adb'
        serial = $Serial
        phase = $script:phase
        arguments = $Arguments
        exitCode = $exitCode
        output = $recordedOutput
    }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "ADB failed for $Serial during $script:phase (exit $exitCode): $($Arguments -join ' ')"
    }
    return ($output -join "`n")
}

function Get-AppPid([string]$Serial) {
    return (Invoke-Adb -Serial $Serial -Arguments @('shell', 'pidof', $package) -AllowFailure -CompactTranscript).Trim()
}

function Assert-SceneLogHealthy([string]$Serial, [string]$Context) {
    $monitor = $script:sceneMonitors[$Serial]
    $stream = if ($monitor -and (Test-Path -LiteralPath $monitor.stdout)) {
        Get-Content -Raw -LiteralPath $monitor.stdout -ErrorAction SilentlyContinue
    } else { '' }
    if ($stream -match 'signal 11|SIGSEGV|FATAL EXCEPTION') {
        throw "$Serial emitted a fatal crash marker during $Context."
    }
    if ($stream -match '(?s)IndexOutOfRangeException.{0,800}Octo\.Asset\.AssetBundleManager') {
        throw "$Serial emitted an Octo AssetBundleManager IndexOutOfRangeException during $Context."
    }
    return $stream
}

function Wait-AllForProcess([int]$TimeoutSeconds) {
    $pending = @{}
    foreach ($serial in $Serials) { $pending[$serial] = $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($serial in @($pending.Keys)) {
            if (Get-AppPid $serial) {
                $pending.Remove($serial)
                Write-JsonLine @{ kind = 'checkpoint'; serial = $serial; phase = $phase; marker = 'process-started' }
            }
        }
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Seconds 1
    }
    throw "App process did not start within $TimeoutSeconds seconds on: $($pending.Keys -join ', ')."
}

function Start-SceneMonitors {
    foreach ($serial in $Serials) {
        $stdout = Join-Path $runDirectory "scene-stream-$serial.txt"
        $stderr = Join-Path $runDirectory "scene-stream-$serial.stderr.txt"
        $process = Start-Process -FilePath $adb `
            -ArgumentList @('-s', $serial, 'logcat', '-v', 'brief', 'Unity:D', 'CRASH:E', 'AndroidRuntime:E', '*:S') `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
            -WindowStyle Hidden -PassThru
        $script:sceneMonitors[$serial] = @{
            process = $process
            stdout = $stdout
            stderr = $stderr
        }
        Write-JsonLine @{ kind = 'scene-monitor-start'; serial = $serial; pid = $process.Id; path = $stdout }
    }
}

function Stop-SceneMonitors {
    foreach ($serial in @($script:sceneMonitors.Keys)) {
        $monitor = $script:sceneMonitors[$serial]
        $process = $monitor.process
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(3000) | Out-Null
        }
        Write-JsonLine @{ kind = 'scene-monitor-stop'; serial = $serial; pid = $process.Id }
    }
}

function Assert-DeviceReady([string]$Serial) {
    $state = (& $adb -s $Serial get-state 2>$null)
    if ($LASTEXITCODE -ne 0 -or $state.Trim() -ne 'device') {
        throw "$Serial is not in ADB device state."
    }
    $boot = (Invoke-Adb -Serial $Serial -Arguments @('shell', 'getprop', 'sys.boot_completed')).Trim()
    if ($boot -ne '1') {
        throw "$Serial has not completed boot."
    }
    $size = Invoke-Adb -Serial $Serial -Arguments @('shell', 'wm', 'size')
    if ($size -notmatch '1080x1920') {
        throw "$Serial has unexpected display size: $size"
    }
}

function Assert-BackendReady {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $BackendReadyUrl -TimeoutSec 10
    } catch {
        throw "Backend readiness check failed at ${BackendReadyUrl}: $($_.Exception.Message)"
    }
    Write-JsonLine @{
        kind = 'backend-readiness'
        phase = $script:phase
        url = $BackendReadyUrl
        statusCode = [int]$response.StatusCode
        body = $response.Content
    }
    if ($response.StatusCode -ne 200) {
        throw "Backend is not ready at $BackendReadyUrl (HTTP $($response.StatusCode))."
    }
}

function Disable-AndroidProxy([string]$Serial) {
    $before = (Invoke-Adb -Serial $Serial -Arguments @('shell', 'settings', 'get', 'global', 'http_proxy') -AllowFailure).Trim()
    # `delete` can leave ConnectivityService using its cached ProxyInfo until a
    # later network transition. Writing Android's explicit no-proxy sentinel
    # forces the settings observer/broadcast path even when `get` said null.
    Invoke-Adb -Serial $Serial -Arguments @('shell', 'settings', 'put', 'global', 'http_proxy', ':0') | Out-Null
    $after = (Invoke-Adb -Serial $Serial -Arguments @('shell', 'settings', 'get', 'global', 'http_proxy')).Trim()
    Write-JsonLine @{
        kind = 'android-proxy'
        serial = $Serial
        phase = $script:phase
        before = $before
        after = $after
    }
    if ($after -and $after -ne 'null' -and $after -ne ':0') {
        throw "$Serial still has Android proxy '$after'; direct battle testing requires no proxy."
    }
}

function Dismiss-SystemAnrDialogIfPresent([string]$Serial) {
    $windows = Invoke-Adb -Serial $Serial -Arguments @('shell', 'dumpsys', 'window', 'windows') -AllowFailure -CompactTranscript
    if ($windows -match '(?m)^\s*mCurrentFocus=.*(?:AppNotRespondingDialog|Application Not Responding|isn.t responding)') {
        # Fixed by the runner's required 1080x1920 display: select the lower
        # "Wait" action instead of terminating System UI or the game.
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'input', 'tap', '330', '1100') | Out-Null
        Write-JsonLine @{ kind = 'system-anr-dismiss'; serial = $Serial; phase = $script:phase; action = 'wait' }
        Start-Sleep -Seconds 3
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
    return $scaled
}

function Get-HomeVisualState([string]$Serial) {
    $remote = "/data/local/tmp/kf-$RunId-home-probe.png"
    $probe = Join-Path $runDirectory ".home-probe-$Serial.png"
    try {
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'screencap', '-p', $remote) | Out-Null
        Invoke-Adb -Serial $Serial -Arguments @('pull', $remote, $probe) | Out-Null
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
        Add-Type -AssemblyName System.Drawing
        $image = [Drawing.Bitmap]::FromFile($probe)
        try {
            if ($image.Width -ne 1080 -or $image.Height -ne 1920) { return 'unknown' }
            $yellowSamples = 0
            for ($x = 730; $x -le 900; $x += 20) {
                for ($y = 1130; $y -le 1370; $y += 20) {
                    $pixel = $image.GetPixel($x, $y)
                    if ($pixel.R -ge 220 -and $pixel.G -ge 160 -and $pixel.B -le 80) {
                        $yellowSamples++
                    }
                }
            }
            if ($yellowSamples -ge 12) { return 'combat' }

            $whiteSamples = 0
            for ($x = 150; $x -le 930; $x += 60) {
                for ($y = 450; $y -le 1450; $y += 60) {
                    $pixel = $image.GetPixel($x, $y)
                    if ($pixel.R -ge 220 -and $pixel.G -ge 220 -and $pixel.B -ge 220) {
                        $whiteSamples++
                    }
                }
            }
            if ($whiteSamples -ge 35) { return 'ranking-modal' }

            # The Notices overlay is mostly black, but has a wide yellow title
            # band near the top, including while its body spinner is active.
            $noticeYellowSamples = 0
            for ($x = 220; $x -le 860; $x += 40) {
                for ($y = 70; $y -le 180; $y += 20) {
                    $pixel = $image.GetPixel($x, $y)
                    if ($pixel.R -ge 220 -and $pixel.G -ge 160 -and $pixel.B -le 80) {
                        $noticeYellowSamples++
                    }
                }
            }
            if ($noticeYellowSamples -ge 12) { return 'notices-modal' }
            return 'loading'
        } finally {
            $image.Dispose()
        }
    } finally {
        if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force }
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
    }
}

function Get-TitleVisualState([string]$Serial) {
    $remote = "/data/local/tmp/kf-$RunId-title-probe.png"
    $probe = Join-Path $runDirectory ".title-probe-$Serial.png"
    try {
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'screencap', '-p', $remote) | Out-Null
        Invoke-Adb -Serial $Serial -Arguments @('pull', $remote, $probe) | Out-Null
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
        Add-Type -AssemblyName System.Drawing
        $image = [Drawing.Bitmap]::FromFile($probe)
        try {
            if ($image.Width -ne 1080 -or $image.Height -ne 1920) { return 'unknown' }
            # The initial LOADING view is black apart from its small logo. The
            # interactive TAP START view renders the arena across most of the
            # framebuffer, so broad brightness is a stable readiness signal.
            $brightSamples = 0
            for ($x = 45; $x -le 1035; $x += 45) {
                for ($y = 45; $y -le 1875; $y += 45) {
                    $pixel = $image.GetPixel($x, $y)
                    if (($pixel.R + $pixel.G + $pixel.B) -ge 180) { $brightSamples++ }
                }
            }
            if ($brightSamples -ge 250) { return 'tap-start-ready' }
            return 'loading'
        } finally {
            $image.Dispose()
        }
    } finally {
        if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force }
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
    }
}

function Wait-AllTitleInteractive([int]$TimeoutSeconds) {
    $pending = @{}
    foreach ($serial in $Serials) { $pending[$serial] = $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($serial in @($pending.Keys)) {
            if (-not (Get-AppPid $serial)) { throw "$serial app process died while Title UI was rendering." }
            Assert-SceneLogHealthy -Serial $serial -Context 'Title UI rendering' | Out-Null
            if ((Get-TitleVisualState -Serial $serial) -eq 'tap-start-ready') {
                $pending.Remove($serial)
                Write-JsonLine @{ kind = 'checkpoint'; serial = $serial; phase = $phase; marker = 'tap-start-visible' }
            }
        }
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Seconds 3
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for rendered TAP START on: $($pending.Keys -join ', ')."
}

function Get-MatchingVisualState([string]$Serial) {
    $remote = "/data/local/tmp/kf-$RunId-matching-probe.png"
    $probe = Join-Path $runDirectory ".matching-probe-$Serial.png"
    try {
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'screencap', '-p', $remote) | Out-Null
        Invoke-Adb -Serial $Serial -Arguments @('pull', $remote, $probe) | Out-Null
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
        Add-Type -AssemblyName System.Drawing
        $image = [Drawing.Bitmap]::FromFile($probe)
        try {
            if ($image.Width -ne 1080 -or $image.Height -ne 1920) { return 'unknown' }
            # The ready roster has eight wide white player rows. Scene-entry
            # animations and the black Cargando screen contain far fewer white
            # samples in this region, even though MatchingScene is logged early.
            $whiteSamples = 0
            for ($x = 90; $x -le 990; $x += 30) {
                for ($y = 300; $y -le 1350; $y += 30) {
                    $pixel = $image.GetPixel($x, $y)
                    if ($pixel.R -ge 210 -and $pixel.G -ge 210 -and $pixel.B -ge 210) {
                        $whiteSamples++
                    }
                }
            }
            if ($whiteSamples -ge 300) { return 'roster-ready' }
            return 'loading'
        } finally {
            $image.Dispose()
        }
    } finally {
        if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force }
        Invoke-Adb -Serial $Serial -Arguments @('shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
    }
}

function Wait-AllMatchingInteractive([int]$TimeoutSeconds) {
    $pending = @{}
    foreach ($serial in $Serials) { $pending[$serial] = $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($serial in @($pending.Keys)) {
            if (-not (Get-AppPid $serial)) { throw "$serial app process died while Matching UI was rendering." }
            Assert-SceneLogHealthy -Serial $serial -Context 'Matching UI rendering' | Out-Null
            $visualState = Get-MatchingVisualState -Serial $serial
            if ($visualState -eq 'roster-ready') {
                $pending.Remove($serial)
                Write-JsonLine @{ kind = 'checkpoint'; serial = $serial; phase = $phase; marker = 'matching-roster-visible' }
            }
        }
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Seconds 3
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for rendered Matching roster on: $($pending.Keys -join ', ')."
}

function Wait-AllHomeInteractive([int]$TimeoutSeconds, [switch]$AllowModal, [switch]$DismissModals) {
    $pending = @{}
    $dismissCounts = @{}
    foreach ($serial in $Serials) { $pending[$serial] = $true }
    foreach ($serial in $Serials) { $dismissCounts[$serial] = 0 }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($serial in @($pending.Keys)) {
            if (-not (Get-AppPid $serial)) { throw "$serial app process died while Home UI was rendering." }
            Assert-SceneLogHealthy -Serial $serial -Context 'Home UI rendering' | Out-Null
            $visualState = Get-HomeVisualState -Serial $serial
            if ($visualState -eq 'combat' -or ($AllowModal -and $visualState -match '-modal$')) {
                $pending.Remove($serial)
                $marker = if ($AllowModal) { 'home-ui-or-modal-visible' } else { 'home-combat-button-visible' }
                Write-JsonLine @{ kind = 'checkpoint'; serial = $serial; phase = $phase; marker = $marker; visualState = $visualState }
            } elseif ($DismissModals -and $visualState -match '-modal$' -and $dismissCounts[$serial] -lt 12) {
                $dismissCounts[$serial]++
                if ($visualState -eq 'ranking-modal') {
                    Invoke-Adb -Serial $serial -Arguments @('shell', 'input', 'tap', '540', '1450') | Out-Null
                } else {
                    Invoke-Adb -Serial $serial -Arguments @('shell', 'input', 'tap', '540', '1785') | Out-Null
                }
                Write-JsonLine @{
                    kind = 'modal-dismiss'
                    serial = $serial
                    phase = $phase
                    visualState = $visualState
                    attempt = $dismissCounts[$serial]
                }
            }
        }
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Seconds 5
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for rendered Home combat button on: $($pending.Keys -join ', ')."
}

function Save-Diagnostics([string]$Reason) {
    foreach ($serial in $Serials) {
        $deviceState = @(& $adb -s $serial get-state 2>$null) -join ''
        if ($deviceState.Trim() -ne 'device') {
            Set-Content -LiteralPath (Join-Path $runDirectory "device-$serial-unavailable.txt") `
                -Value "ADB state unavailable during ${phase}: $deviceState"
            continue
        }
        try { Save-Screenshot -Serial $serial -Name "failure-$phase" | Out-Null } catch {}
        $fullLines = @(& $adb -s $serial logcat -d -v threadtime 2>&1)
        $full = $fullLines -join "`n"
        Write-JsonLine @{
            kind = 'adb-logcat-dump'
            serial = $serial
            phase = $phase
            exitCode = $LASTEXITCODE
            lineCount = $fullLines.Count
            characterCount = $full.Length
        }
        Set-Content -LiteralPath (Join-Path $runDirectory "logcat-$serial-full.txt") -Value $full
        $filtered = $fullLines | Select-String -Pattern 'Unity|GameScene|MatchingScene|HomeScene|TitleScene|SIGSEGV|signal 11|FATAL|AndroidRuntime|NullReference|IndexOutOfRange|AssetBundleManager|Exception|Forceddisconnect|UpdateReconnect|UpdateNoInputTime|libunity|libil2cpp' -CaseSensitive:$false
        Set-Content -LiteralPath (Join-Path $runDirectory "logcat-$serial-filtered.txt") -Value $filtered
        Set-Content -LiteralPath (Join-Path $runDirectory "activity-$serial.txt") -Value (Invoke-Adb -Serial $serial -Arguments @('shell', 'dumpsys', 'activity', 'activities') -AllowFailure)
        Set-Content -LiteralPath (Join-Path $runDirectory "package-$serial.txt") -Value (Invoke-Adb -Serial $serial -Arguments @('shell', 'dumpsys', 'package', $package) -AllowFailure)
        Set-Content -LiteralPath (Join-Path $runDirectory "process-$serial.txt") -Value (Invoke-Adb -Serial $serial -Arguments @('shell', 'ps', '-A') -AllowFailure | Select-String -Pattern 'kickflight|ndk_translation|crash')
    }
    Write-JsonLine @{ kind = 'diagnostics'; phase = $phase; reason = $Reason }
}

function Wait-AllForLogMarker {
    param(
        [Parameter(Mandatory)][string]$Marker,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [switch]$RequireAlive,
        [int]$RetryTapX = -1,
        [int]$RetryTapY = -1,
        [int]$RetrySecondaryX = -1,
        [int]$RetrySecondaryY = -1,
        [int]$RetryEverySeconds = 20,
        [int]$MaxRetries = 2
    )

    $pending = @{}
    $retryCounts = @{}
    $nextRetry = @{}
    $nextAnrCheck = @{}
    $downloadHandled = @{}
    foreach ($serial in $Serials) { $pending[$serial] = $true }
    foreach ($serial in $Serials) {
        $retryCounts[$serial] = 0
        $nextRetry[$serial] = (Get-Date).AddSeconds($RetryEverySeconds)
        $nextAnrCheck[$serial] = Get-Date
        $downloadHandled[$serial] = $false
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        # A device that already reached this marker can still crash while its
        # peer is loading. Check the full pair on every cycle so the run fails
        # immediately instead of consuming the remaining phase timeout.
        if ($RequireAlive) {
            foreach ($serial in $Serials) {
                if (-not (Get-AppPid $serial)) {
                    throw "$serial app process died while waiting for $Marker."
                }
            }
        }
        foreach ($serial in @($pending.Keys)) {
            if ((Get-Date) -ge $nextAnrCheck[$serial]) {
                Dismiss-SystemAnrDialogIfPresent $serial
                $nextAnrCheck[$serial] = (Get-Date).AddSeconds(15)
            }
            if (-not $script:sceneMonitors[$serial]) { throw "No scene monitor exists for $serial." }
            $sceneLog = Assert-SceneLogHealthy -Serial $serial -Context "wait for $Marker"
            if ($sceneLog -match [regex]::Escape($Marker)) {
                $pending.Remove($serial)
                Write-JsonLine @{ kind = 'checkpoint'; serial = $serial; phase = $phase; marker = $Marker }
            } elseif ($Marker -eq 'HomeScene' -and -not $downloadHandled[$serial] -and $sceneLog -match 'DownloadScene') {
                $downloadHandled[$serial] = $true
                Write-JsonLine @{ kind = 'checkpoint'; serial = $serial; phase = $phase; marker = 'DownloadScene' }
                # DownloadScene can appear well after the fixed post-Title tap.
                # Give its confirmation view time to render, then accept once.
                Start-Sleep -Seconds 3
                Invoke-Adb -Serial $serial -Arguments @('shell', 'input', 'tap', '765', '1135') | Out-Null
                Write-JsonLine @{ kind = 'download-confirm'; serial = $serial; phase = $phase; x = 765; y = 1135 }
            } elseif ($RetryTapX -ge 0 -and (Get-Date) -ge $nextRetry[$serial] -and $retryCounts[$serial] -lt $MaxRetries) {
                $retryCounts[$serial]++
                Invoke-Adb -Serial $serial -Arguments @('shell', 'input', 'tap', "$RetryTapX", "$RetryTapY") | Out-Null
                if ($RetrySecondaryX -ge 0) {
                    Start-Sleep -Seconds 2
                    Invoke-Adb -Serial $serial -Arguments @('shell', 'input', 'tap', "$RetrySecondaryX", "$RetrySecondaryY") | Out-Null
                }
                $nextRetry[$serial] = (Get-Date).AddSeconds($RetryEverySeconds)
                Write-JsonLine @{ kind = 'bounded-retry'; serial = $serial; phase = $phase; marker = $Marker; attempt = $retryCounts[$serial] }
            }
        }
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Seconds 3
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for $Marker on: $($pending.Keys -join ', ')."
}

function Wait-AllStable([int]$DurationSeconds, [string]$Context) {
    $deadline = (Get-Date).AddSeconds($DurationSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($serial in $Serials) {
            if (-not (Get-AppPid $serial)) {
                throw "$serial app process died during $Context."
            }
            Assert-SceneLogHealthy -Serial $serial -Context $Context | Out-Null
        }
        Start-Sleep -Seconds 3
    }
}

function Tap-All([int]$X, [int]$Y, [string]$Action) {
    $script:phase = $Action
    foreach ($serial in $Serials) {
        Invoke-Adb -Serial $serial -Arguments @('shell', 'input', 'tap', "$X", "$Y") | Out-Null
    }
}

New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

# Associate the server-side request trace with this run. Only bytes appended
# after runner startup are copied, keeping failure bundles small and directly
# attributable to the current two-device attempt.
$serviceStatePath = Join-Path $repo '.local\session-logs\kickflight-services.json'
if (Test-Path -LiteralPath $serviceStatePath -PathType Leaf) {
    $serviceState = Get-Content -Raw -LiteralPath $serviceStatePath | ConvertFrom-Json
    foreach ($serverLog in @(
        @{ path = $serviceState.server.stdout; outputName = 'server-stdout.txt' },
        @{ path = $serviceState.server.stderr; outputName = 'server-stderr.txt' }
    )) {
        if ($serverLog.path -and (Test-Path -LiteralPath $serverLog.path -PathType Leaf)) {
            $script:serverLogSources += @{
                path = $serverLog.path
                outputName = $serverLog.outputName
                startLength = (Get-Item -LiteralPath $serverLog.path).Length
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $adb -PathType Leaf)) {
    throw "ADB not found: $adb"
}
if ($Serials.Count -ne 2 -or ($Serials | Select-Object -Unique).Count -ne 2) {
    throw 'Exactly two distinct device serials are required.'
}
if (-not $SkipInstall) {
    if (-not $ApkPath) { throw 'Provide -ApkPath or use -SkipInstall.' }
    $ApkPath = [IO.Path]::GetFullPath((Join-Path $repo $ApkPath))
    if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) { throw "APK not found: $ApkPath" }
}
if ($ValidateOnly) {
    Write-JsonLine @{ kind = 'validation'; result = 'ok'; serials = $Serials; target = $Target }
    Write-Host "Runner validation passed. Evidence directory: $runDirectory"
    exit 0
}

$result = 'failed'
$failure = $null
try {
    if ($RestartEmulators) {
        $phase = 'restart-emulators'
        & (Join-Path $PSScriptRoot 'restart-test-emulators.ps1') `
            -RunId $RunId -EvidenceRoot $EvidenceRoot -Visible:$VisibleEmulators
        if ($LASTEXITCODE -ne 0) { throw 'Emulator restart helper failed.' }
    }
    $phase = 'preflight'
    Assert-BackendReady
    foreach ($serial in $Serials) { Assert-DeviceReady $serial }
    foreach ($serial in $Serials) { Disable-AndroidProxy $serial }
    # Cold API 35 boots can report boot_completed before System UI and the
    # translated ARM runtime are responsive enough to launch this Unity game.
    if ($RestartEmulators) { Start-Sleep -Seconds 20 }
    foreach ($serial in $Serials) { Dismiss-SystemAnrDialogIfPresent $serial }

    if ($ResetAppData) {
        $phase = 'reset-app-data'
        foreach ($serial in $Serials) {
            Invoke-Adb -Serial $serial -Arguments @('shell', 'am', 'force-stop', $package) | Out-Null
            $clearResult = Invoke-Adb -Serial $serial -Arguments @('shell', 'pm', 'clear', $package)
            if ($clearResult.Trim() -ne 'Success') {
                throw "Failed to reset app data on ${serial}: $clearResult"
            }
            Write-JsonLine @{ kind = 'app-data-reset'; serial = $serial; phase = $phase; package = $package }
        }
    }

    if ($SeedAssetCache) {
        $phase = 'seed-asset-cache'
        $seedScript = Join-Path $PSScriptRoot 'seed-device-cache.py'
        $platformTools = Split-Path -Parent $adb
        $previousPath = $env:PATH
        try {
            $env:PATH = "$platformTools;$previousPath"
            foreach ($serial in $Serials) {
                $seedOutput = @(& python $seedScript --device $serial --skip-tar 2>&1)
                $seedExitCode = $LASTEXITCODE
                Write-JsonLine @{
                    kind = 'asset-cache-seed'
                    serial = $serial
                    phase = $phase
                    exitCode = $seedExitCode
                    output = ($seedOutput -join "`n")
                }
                if ($seedExitCode -ne 0) {
                    throw "Asset cache seeding failed for $serial (exit $seedExitCode)."
                }

                $cacheCount = Invoke-Adb -Serial $serial -Arguments @(
                    'shell', 'find', "/data/data/$package/files/octo", '-type', 'f', '|', 'wc', '-l'
                )
                Write-JsonLine @{
                    kind = 'asset-cache-verified'
                    serial = $serial
                    phase = $phase
                    fileCount = $cacheCount.Trim()
                }
                if ([int]$cacheCount.Trim() -lt 5187) {
                    throw "Asset cache verification failed for ${serial}: only $($cacheCount.Trim()) files."
                }
            }
        } finally {
            $env:PATH = $previousPath
        }
    }

    if (-not $SkipInstall) {
        $phase = 'install'
        $localHash = (Get-FileHash -LiteralPath $ApkPath -Algorithm SHA256).Hash
        foreach ($serial in $Serials) {
            Invoke-Adb -Serial $serial -Arguments @('shell', 'am', 'force-stop', $package) | Out-Null
            Invoke-Adb -Serial $serial -Arguments @('install', '-r', $ApkPath) | Out-Null
            $installedPath = (Invoke-Adb -Serial $serial -Arguments @('shell', 'pm', 'path', $package)).Replace('package:', '').Trim()
            $deviceHash = (Invoke-Adb -Serial $serial -Arguments @('shell', 'sha256sum', $installedPath)).Split(' ')[0].ToUpperInvariant()
            if ($deviceHash -ne $localHash) { throw "Installed APK hash mismatch on $serial." }
        }
    }

    $phase = 'launch'
    foreach ($serial in $Serials) {
        Invoke-Adb -Serial $serial -Arguments @('shell', 'pm', 'grant', $package, 'android.permission.POST_NOTIFICATIONS') -AllowFailure | Out-Null
        Invoke-Adb -Serial $serial -Arguments @('shell', 'am', 'force-stop', $package) | Out-Null
        Invoke-Adb -Serial $serial -Arguments @('logcat', '-c') | Out-Null
    }
    Start-SceneMonitors
    for ($serialIndex = 0; $serialIndex -lt $Serials.Count; $serialIndex++) {
        $serial = $Serials[$serialIndex]
        Invoke-Adb -Serial $serial -Arguments @('shell', 'am', 'start', '-n', $activity) | Out-Null
        if ($serialIndex -lt ($Serials.Count - 1) -and $LaunchStaggerSeconds -gt 0) {
            $staggerDeadline = (Get-Date).AddSeconds($LaunchStaggerSeconds)
            while ((Get-Date) -lt $staggerDeadline) {
                Dismiss-SystemAnrDialogIfPresent $serial
                Start-Sleep -Seconds 5
            }
            Write-JsonLine @{ kind = 'launch-stagger'; serial = $serial; phase = $phase; seconds = $LaunchStaggerSeconds }
        }
    }

    $phase = 'wait-process'
    Wait-AllForProcess -TimeoutSeconds 30
    $phase = 'wait-title'
    Wait-AllForLogMarker -Marker 'TitleScene' -TimeoutSeconds $timeouts.TitleScene -RequireAlive
    $phase = 'wait-title-ui'
    Wait-AllTitleInteractive -TimeoutSeconds 180
    foreach ($serial in $Serials) { Save-Screenshot $serial 'title' | Out-Null }

    Tap-All 540 1200 'tap-start'
    Start-Sleep -Seconds 8
    # This coordinate is inert when the cached client has no Download modal.
    Tap-All 765 1135 'dismiss-download-if-present'

    $phase = 'wait-home'
    Wait-AllForLogMarker -Marker 'HomeScene' -TimeoutSeconds $timeouts.HomeScene -RequireAlive `
        -RetryTapX 540 -RetryTapY 1200 -RetrySecondaryX 765 -RetrySecondaryY 1135
    $phase = 'wait-home-ui'
    Wait-AllHomeInteractive -TimeoutSeconds 150 -AllowModal
    foreach ($serial in $Serials) { Save-Screenshot $serial 'home-before-modals' | Out-Null }

    # Known first-login overlays: ranking acceptance, then Notices close button.
    Tap-All 540 1450 'dismiss-ranking-if-present'
    Start-Sleep -Seconds 2
    Tap-All 540 1785 'dismiss-notices-if-present'
    Start-Sleep -Seconds 2
    $phase = 'wait-home-combat-button'
    Wait-AllHomeInteractive -TimeoutSeconds 90 -DismissModals
    foreach ($serial in $Serials) { Save-Screenshot $serial 'home-ready' | Out-Null }

    Tap-All 820 1240 'open-combat'
    $phase = 'wait-matching'
    Wait-AllForLogMarker -Marker 'MatchingScene' -TimeoutSeconds $timeouts.MatchingScene -RequireAlive `
        -RetryTapX 820 -RetryTapY 1240

    if ($Target -eq 'GameScene') {
        # Match the successful dual-device trace: capture once, tap once, then
        # leave Unity's main thread alone while Stage 1/2/3 are delivered.
        # Repeated screencap/pull polling during this short window made both
        # clients acknowledge Stage 3 without opening Photon on slower AVDs.
        foreach ($serial in $Serials) { Save-Screenshot $serial 'matching' | Out-Null }
        Tap-All 540 350 'prime-battle-start'
        $phase = 'wait-game'
        Wait-AllForLogMarker -Marker 'GameScene' -TimeoutSeconds $timeouts.GameScene -RequireAlive `
            -RetryTapX 540 -RetryTapY 350
        $phase = 'settle-game'
        Wait-AllStable -DurationSeconds $GameSettleSeconds -Context 'GameScene settle period'
        foreach ($serial in $Serials) { Save-Screenshot $serial 'game-entry' | Out-Null }
    } else {
        $phase = 'wait-matching-ui'
        Wait-AllMatchingInteractive -TimeoutSeconds 180
        foreach ($serial in $Serials) { Save-Screenshot $serial 'matching' | Out-Null }
    }

    $result = 'passed'
} catch {
    $failure = $_.Exception.Message
    Save-Diagnostics -Reason $failure
} finally {
    Stop-SceneMonitors
    Save-ServerLogSlices
    $deviceResults = @{}
    foreach ($serial in $Serials) {
        $deviceResults[$serial] = @{ pid = (Get-AppPid $serial); state = (& $adb -s $serial get-state 2>$null) }
    }
    @{
        runId = $RunId
        result = $result
        target = $Target
        finalPhase = $phase
        failure = $failure
        apkPath = $ApkPath
        devices = $deviceResults
        evidenceDirectory = $runDirectory
        completedAt = (Get-Date).ToString('o')
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath
}

if ($result -ne 'passed') {
    Write-Error "Battle-entry runner failed during '$phase': $failure. Evidence: $runDirectory"
    exit 1
}
Write-Host "Battle-entry runner passed through $Target. Evidence: $runDirectory"
