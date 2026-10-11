[CmdletBinding()]
param(
    [string]$SourceApk = 'C:\Users\Gixar\Documentos\Variedad\Kick-Flight-Assets\base.apk',
    [string]$ConfigPath = 'config\apk-direct-server.local.json',
    [string]$OutputPath,
    [bool]$ForceGameScene = $true,
    [switch]$EnableExperimentalUnityAllocators,
    [switch]$KeepWorkDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Resolve-RepositoryPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $repo $Path))
}

$source = Resolve-RepositoryPath $SourceApk
$configFile = Resolve-RepositoryPath $ConfigPath
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Source APK not found: $source"
}
if (-not (Test-Path -LiteralPath $configFile -PathType Leaf)) {
    throw "Direct-server config not found: $configFile"
}

$config = Get-Content -Raw -LiteralPath $configFile | ConvertFrom-Json
$baseUri = [Uri]$config.serverBaseUrl
if ($baseUri.Scheme -ne 'http' -or -not $baseUri.Host -or $baseUri.AbsolutePath -ne '/') {
    throw 'serverBaseUrl must have the form http://host:port with no path, query, or fragment.'
}

$actualSourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($actualSourceHash -ne $config.expectedSourceSha256) {
    throw "Source APK hash mismatch. Expected $($config.expectedSourceSha256), found $actualSourceHash."
}

$apktool = Join-Path $repo '.local\tools\apktool_3.0.3.jar'
$buildTools = Join-Path $repo '.local\android-sdk\build-tools\35.0.0'
$patcher = Join-Path $PSScriptRoot 'patch-il2cpp-endpoints.py'
$zipalign = Join-Path $buildTools 'zipalign.exe'
$apksigner = Join-Path $buildTools 'apksigner.bat'
$keystore = Join-Path $repo '.local\kickflight-test-signing.jks'
$keytool = 'C:\Program Files\Java\jdk-25\bin\keytool.exe'
foreach ($required in $apktool, $patcher, $zipalign, $apksigner, $keytool) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required build tool not found: $required"
    }
}

$artifactDirectory = Join-Path $repo '.local\artifacts'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
if (-not $OutputPath) {
    $safeHost = $baseUri.Host.Replace(':', '-').Replace('[', '').Replace(']', '')
    $OutputPath = Join-Path $artifactDirectory "KickFlight-2.11.0-direct-$safeHost.apk"
} else {
    $OutputPath = Resolve-RepositoryPath $OutputPath
}

$workRoot = Join-Path $repo '.local\apk-direct-work'
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
$work = Join-Path $workRoot ([Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $work | Out-Null
$decoded = Join-Path $work 'decoded'
$unsigned = Join-Path $work 'unsigned.apk'
$aligned = Join-Path $work 'aligned.apk'

try {
    & java -jar $apktool d -s $source -o $decoded
    if ($LASTEXITCODE -ne 0) { throw "Apktool decode failed with exit code $LASTEXITCODE." }

    # versionName 2.11.1 (2.11.1-diag for KF_DIAG=1 builds, let through server maintenance) / versionCode 56
    $versionFlags = @(if ($env:KF_DIAG -eq '1') { '--diag' })
    & python (Join-Path $repo 'scripts\set-apk-version.py') $decoded @versionFlags
    if ($LASTEXITCODE -ne 0) { throw "Version stamp failed with exit code $LASTEXITCODE." }

    $previousForceGameScene = $env:KF_FORCE_GAME_SCENE
    $previousExperimentalAllocators = $env:KF_UNITY_EXPERIMENTAL_ALLOCATORS
    try {
        $env:KF_FORCE_GAME_SCENE = if ($ForceGameScene) { '1' } else { '0' }
        $env:KF_UNITY_EXPERIMENTAL_ALLOCATORS = if ($EnableExperimentalUnityAllocators) { '1' } else { '0' }
        & python $patcher `
            --metadata (Join-Path $decoded 'assets\bin\Data\Managed\Metadata\global-metadata.dat') `
            --arm64 (Join-Path $decoded 'lib\arm64-v8a\libil2cpp.so') `
            --armv7 (Join-Path $decoded 'lib\armeabi-v7a\libil2cpp.so') `
            --arm64-unity (Join-Path $decoded 'lib\arm64-v8a\libunity.so') `
            --base-url $config.serverBaseUrl
        if ($LASTEXITCODE -ne 0) { throw "IL2CPP endpoint patch failed with exit code $LASTEXITCODE." }
    } finally {
        $env:KF_FORCE_GAME_SCENE = $previousForceGameScene
        $env:KF_UNITY_EXPERIMENTAL_ALLOCATORS = $previousExperimentalAllocators
    }

    # Previous-session exit reporter: uploads native crashes / ANRs / LMK kills to <server>/diag/upload at next start.
    & python (Join-Path $repo 'scripts\patch-exit-reporter.py') $decoded --base-url $config.serverBaseUrl
    if ($LASTEXITCODE -ne 0) { throw "Exit reporter patch failed with exit code $LASTEXITCODE." }

    & java -jar $apktool b $decoded -o $unsigned
    if ($LASTEXITCODE -ne 0) { throw "Apktool build failed with exit code $LASTEXITCODE." }

    if (-not (Test-Path -LiteralPath $keystore -PathType Leaf)) {
        & $keytool -genkeypair -keystore $keystore -storepass android -keypass android `
            -alias kickflight-test -keyalg RSA -keysize 2048 -validity 10000 `
            -dname 'CN=KickFlight Local Test,OU=Development,O=Local,C=MX'
        if ($LASTEXITCODE -ne 0) { throw "Development signing-key generation failed with exit code $LASTEXITCODE." }
    }

    & $zipalign -p -f 4 $unsigned $aligned
    if ($LASTEXITCODE -ne 0) { throw "zipalign failed with exit code $LASTEXITCODE." }

    & $apksigner sign --ks $keystore --ks-key-alias kickflight-test `
        --ks-pass pass:android --key-pass pass:android `
        --v1-signing-enabled true --v2-signing-enabled true `
        --out $OutputPath $aligned
    if ($LASTEXITCODE -ne 0) { throw "APK signing failed with exit code $LASTEXITCODE." }

    & $apksigner verify --verbose $OutputPath
    if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed with exit code $LASTEXITCODE." }
    & $zipalign -c -p 4 $OutputPath
    if ($LASTEXITCODE -ne 0) { throw "APK alignment verification failed with exit code $LASTEXITCODE." }
} finally {
    if (-not $KeepWorkDirectory -and (Test-Path -LiteralPath $work)) {
        $resolvedWork = [IO.Path]::GetFullPath($work)
        $resolvedRoot = [IO.Path]::GetFullPath($workRoot) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedWork.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected work directory: $resolvedWork"
        }
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}

$outputHash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
Write-Host "Direct APK: $OutputPath"
Write-Host "SHA-256:    $outputHash"
Write-Host "Server URL: $($config.serverBaseUrl)"
Write-Host "Direct host: $($baseUri.Host)"
Write-Host "Forced GameScene transition: $ForceGameScene"
Write-Host "Experimental Unity allocators: $($EnableExperimentalUnityAllocators.IsPresent)"
