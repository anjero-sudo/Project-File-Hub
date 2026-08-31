param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$appProject = Join-Path $projectRoot 'src\ProjectFileHub.App\ProjectFileHub.App.csproj'
$nugetConfig = Join-Path $projectRoot 'NuGet.Config'
$propsPath = Join-Path $projectRoot 'Directory.Build.props'

[xml]$props = Get-Content -LiteralPath $propsPath
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props 中的版本不是 x.y.z：$version"
}
$targetFramework = [string]$props.Project.PropertyGroup.TargetFramework
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw 'Directory.Build.props 缺少 TargetFramework。'
}

$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\release'))
$expectedReleaseRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if (-not $releaseRoot.StartsWith($expectedReleaseRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录超出了项目 artifacts 范围：$releaseRoot"
}

$publishPath = Join-Path $releaseRoot "ProjectFileHub-$version-$Runtime"
$zipPath = Join-Path $releaseRoot "ProjectFileHub-$version-$Runtime.zip"
$hashPath = "$zipPath.sha256"

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $hashPath) {
    Remove-Item -LiteralPath $hashPath -Force
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget-packages'

& $dotnet restore $appProject --configfile $nugetConfig --runtime $Runtime --disable-parallel --disable-build-servers
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish $appProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --no-restore `
    --output $publishPath `
    -p:UseSharedCompilation=false `
    --disable-build-servers
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$buildOutputPath = Join-Path $projectRoot "src\ProjectFileHub.App\bin\$Configuration\$targetFramework\$Runtime"
$compiledXamlFiles = @(Get-ChildItem -LiteralPath $buildOutputPath -Recurse -File -Filter '*.xbf')
if ($compiledXamlFiles.Count -eq 0) {
    throw "构建输出中没有找到 WinUI XBF 资源：$buildOutputPath"
}

foreach ($compiledXamlFile in $compiledXamlFiles) {
    $relativePath = [System.IO.Path]::GetRelativePath($buildOutputPath, $compiledXamlFile.FullName)
    $destinationPath = Join-Path $publishPath $relativePath
    $destinationDirectory = Split-Path -Parent $destinationPath
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $compiledXamlFile.FullName -Destination $destinationPath -Force
}

$projectResourceIndex = Join-Path $buildOutputPath 'ProjectFileHub.pri'
if (-not (Test-Path -LiteralPath $projectResourceIndex)) {
    throw "构建输出缺少应用资源索引：$projectResourceIndex"
}
Copy-Item -LiteralPath $projectResourceIndex -Destination (Join-Path $publishPath 'ProjectFileHub.pri') -Force

$requiredWinUiResources = @(
    'App.xbf',
    'MainWindow.xbf',
    'Themes\FocusCanvas.xbf',
    'ProjectFileHub.pri'
)
$missingWinUiResources = @($requiredWinUiResources | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $publishPath $_))
})
if ($missingWinUiResources.Count -gt 0) {
    throw "发布输出缺少 WinUI 资源：$($missingWinUiResources -join ', ')"
}

$appPath = Join-Path $publishPath 'ProjectFileHub.exe'
if (-not (Test-Path -LiteralPath $appPath)) {
    throw "发布输出缺少主程序：$appPath"
}

$runningInstances = @(Get-Process -Name 'ProjectFileHub' -ErrorAction SilentlyContinue)
if ($runningInstances.Count -gt 0) {
    $runningSummary = ($runningInstances | ForEach-Object { "PID $($_.Id): $($_.Path)" }) -join '; '
    throw "启动冒烟测试前必须完全退出正在运行的 Project File Hub。$runningSummary"
}

$smokeProcess = $null
try {
    $smokeProcess = Start-Process `
        -FilePath $appPath `
        -WorkingDirectory $publishPath `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $smokeProcess.Refresh()
        if ($smokeProcess.HasExited) {
            throw "发布程序在启动冒烟测试期间提前退出，退出代码：$($smokeProcess.ExitCode)"
        }
    } while (($smokeProcess.MainWindowHandle -eq 0 -or $smokeProcess.MainWindowTitle -ne 'Project File Hub') `
        -and [DateTime]::UtcNow -lt $deadline)

    if ($smokeProcess.MainWindowHandle -eq 0 -or $smokeProcess.MainWindowTitle -ne 'Project File Hub') {
        throw "发布程序未在 20 秒内创建 Project File Hub 主窗口。"
    }

    Start-Sleep -Seconds 3
    $smokeProcess.Refresh()
    if ($smokeProcess.HasExited -or -not $smokeProcess.Responding) {
        throw "发布程序创建窗口后未保持稳定响应。"
    }

    Write-Host "Startup smoke:  passed (PID $($smokeProcess.Id))"
}
finally {
    if ($null -ne $smokeProcess) {
        $smokeProcess.Refresh()
        if (-not $smokeProcess.HasExited) {
            Stop-Process -Id $smokeProcess.Id -Force
            [void]$smokeProcess.WaitForExit(5000)
        }
    }
}

Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $hashPath -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii

Write-Host "Release package: $zipPath"
Write-Host "SHA-256:       $hashPath"
