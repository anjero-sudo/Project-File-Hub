param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [string]$PublishPath,

    [string]$InnoCompilerPath,

    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $projectRoot 'Directory.Build.props'
$installerScript = Join-Path $projectRoot 'installer\ProjectFileHub.iss'

[xml]$props = Get-Content -LiteralPath $propsPath
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props 中的版本不是 x.y.z：$version"
}
$versionQuad = "$version.0"

$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'release'))
if (-not $releaseRoot.StartsWith(
        $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录超出了项目 artifacts 范围：$releaseRoot"
}

if ([string]::IsNullOrWhiteSpace($PublishPath)) {
    $PublishPath = Join-Path $releaseRoot "ProjectFileHub-$version-$Runtime"
}
$publishFullPath = [System.IO.Path]::GetFullPath($PublishPath)
if (-not $publishFullPath.StartsWith(
        $releaseRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "安装器输入必须位于项目 release 目录：$publishFullPath"
}

if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw "缺少 Inno Setup 脚本：$installerScript"
}
if (-not (Test-Path -LiteralPath $publishFullPath -PathType Container)) {
    throw "缺少自包含发布目录，请先运行 eng/package-release.ps1：$publishFullPath"
}

$requiredPayloadFiles = @(
    'ProjectFileHub.exe',
    'ProjectFileHub.dll',
    'App.xbf',
    'MainWindow.xbf',
    'Themes\FocusCanvas.xbf',
    'ProjectFileHub.pri'
)
$missingPayloadFiles = @($requiredPayloadFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $publishFullPath $_) -PathType Leaf)
})
if ($missingPayloadFiles.Count -gt 0) {
    throw "安装器输入缺少应用文件：$($missingPayloadFiles -join ', ')"
}

$appPath = Join-Path $publishFullPath 'ProjectFileHub.exe'
$appFileVersion = ([string](Get-Item -LiteralPath $appPath).VersionInfo.FileVersion).Trim()
if ($appFileVersion -ne $versionQuad) {
    throw "发布 EXE 版本 $appFileVersion 与安装器版本 $versionQuad 不一致。"
}

function Resolve-InnoCompiler {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $requestedFullPath = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $requestedFullPath -PathType Leaf)) {
            throw "指定的 Inno Setup 编译器不存在：$requestedFullPath"
        }
        return $requestedFullPath
    }

    if (-not [string]::IsNullOrWhiteSpace($env:INNO_SETUP_COMPILER)) {
        $environmentPath = [System.IO.Path]::GetFullPath($env:INNO_SETUP_COMPILER)
        if (Test-Path -LiteralPath $environmentPath -PathType Leaf) {
            return $environmentPath
        }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    $innoUninstallRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($registryRoot in $innoUninstallRoots) {
        if (-not (Test-Path -LiteralPath $registryRoot)) {
            continue
        }
        foreach ($key in Get-ChildItem -LiteralPath $registryRoot -ErrorAction SilentlyContinue) {
            $entry = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $entry -or
                [string]$entry.DisplayName -notlike 'Inno Setup version 6*' -or
                [string]::IsNullOrWhiteSpace([string]$entry.InstallLocation)) {
                continue
            }

            $registeredCompiler = Join-Path ([string]$entry.InstallLocation) 'ISCC.exe'
            if (Test-Path -LiteralPath $registeredCompiler -PathType Leaf) {
                return [System.IO.Path]::GetFullPath($registeredCompiler)
            }
        }
    }

    $candidatePaths = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        'C:\ProgramData\chocolatey\bin\ISCC.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidatePath)
        }
    }

    throw '未找到 Inno Setup 命令行编译器 ISCC.exe。请安装 Inno Setup 6，或设置 INNO_SETUP_COMPILER。'
}

$compilerPath = Resolve-InnoCompiler -RequestedPath $InnoCompilerPath
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

$outputName = "ProjectFileHub-Setup-$version-$Runtime.exe"
$installerPath = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot $outputName))
$hashPath = "$installerPath.sha256"

foreach ($generatedPath in @($installerPath, $hashPath)) {
    $generatedFullPath = [System.IO.Path]::GetFullPath($generatedPath)
    if (-not $generatedFullPath.StartsWith(
            $releaseRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理 release 目录之外的路径：$generatedFullPath"
    }
    if (Test-Path -LiteralPath $generatedFullPath) {
        Remove-Item -LiteralPath $generatedFullPath -Force
    }
}

$compilerArguments = @(
    '/Qp',
    "/DMyAppVersion=$version",
    "/DMyAppVersionQuad=$versionQuad",
    "/DPublishDir=$publishFullPath",
    "/DOutputDir=$releaseRoot",
    $installerScript
)

& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup 未生成预期安装器：$installerPath"
}

$installerFileVersion = ([string](Get-Item -LiteralPath $installerPath).VersionInfo.FileVersion).Trim()
if ($installerFileVersion -ne $versionQuad) {
    throw "安装器文件版本 $installerFileVersion 与应用版本 $versionQuad 不一致。"
}

$signature = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($RequireSignature -and $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "安装器没有有效的 Authenticode 签名：$($signature.Status)"
}
if ($signature.Status -notin @(
        [System.Management.Automation.SignatureStatus]::Valid,
        [System.Management.Automation.SignatureStatus]::NotSigned)) {
    throw "安装器签名状态异常：$($signature.Status)"
}

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$installerHash  $outputName" -Encoding ascii

$installerFile = Get-Item -LiteralPath $installerPath
Write-Host "Installer:       $installerPath"
Write-Host "Installer bytes: $($installerFile.Length)"
Write-Host "SHA-256:         $installerHash"
Write-Host "Signature:       $($signature.Status)"
