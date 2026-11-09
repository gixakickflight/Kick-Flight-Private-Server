[CmdletBinding()]
param(
    [string]$ApkPath = 'C:\Users\Gixar\Documentos\Variedad\Kick-Flight-Assets\base.apk',
    [string]$AdbPath = 'adb',
    [string]$Serial,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$localAdb = Join-Path $repo '.local\tools\platform-tools\adb.exe'
if ($AdbPath -eq 'adb' -and -not (Get-Command adb -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $localAdb)) { $AdbPath = $localAdb }
& (Join-Path $PSScriptRoot 'verify-apk-hash.ps1') -ApkPath $ApkPath
$adbArgs = @()
if ($Serial) { $adbArgs += @('-s', $Serial) }
if ($Install) {
    & $AdbPath @adbArgs install -r $ApkPath
    if ($LASTEXITCODE -ne 0) { throw 'APK installation failed.' }
}

$captureDirectory = Join-Path $repo 'captures'
New-Item -ItemType Directory -Force -Path $captureDirectory | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$output = Join-Path $captureDirectory "logcat-$stamp.redacted.log"
& $AdbPath @adbArgs logcat -c
& $AdbPath @adbArgs shell am force-stop world.global.fenix
& $AdbPath @adbArgs shell monkey -p world.global.fenix -c android.intent.category.LAUNCHER 1 | Out-Null
Write-Output "App launched. Capturing redacted logcat to $output. Press Ctrl+C to stop."
& $AdbPath @adbArgs logcat -v threadtime | ForEach-Object {
    $_ `
      -replace '(?i)(x-app-access-token|authorization|cookie|x-octo-key|x-api-key)(\s*[:=]\s*)\S+', '$1$2<redacted>' `
      -replace '(?i)(uuid|userUniqueId|advertisingId|adid|idfa)(\s*[:=]\s*)[A-Za-z0-9._:-]+', '$1$2<redacted>'
} | Tee-Object -FilePath $output
