param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [string]$InstallerPath,

    [switch]$SkipLaunch,

    [switch]$SkipShortcuts
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $projectRoot 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props 中的版本不是 x.y.z：$version"
}
$versionQuad = "$version.0"

$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'release'))
$verificationRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'verification'))
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $releaseRoot "ProjectFileHub-Setup-$version-$Runtime.exe"
}
$installerFullPath = [System.IO.Path]::GetFullPath($InstallerPath)
if (-not $installerFullPath.StartsWith(
        $releaseRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "安装器测试输入必须位于项目 release 目录：$installerFullPath"
}
if (-not (Test-Path -LiteralPath $installerFullPath -PathType Leaf)) {
    throw "缺少安装器：$installerFullPath"
}

$runningInstances = @(Get-Process -Name 'ProjectFileHub' -ErrorAction SilentlyContinue)
if ($runningInstances.Count -gt 0) {
    $runningSummary = ($runningInstances | ForEach-Object { "PID $($_.Id): $($_.Path)" }) -join '; '
    throw "安装器启动测试前必须完全退出正在运行的 Project File Hub。$runningSummary"
}

function Get-ProjectFileHubUninstallEntries {
    $registryRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    $entries = foreach ($registryRoot in $registryRoots) {
        if (-not (Test-Path -LiteralPath $registryRoot)) {
            continue
        }
        foreach ($key in Get-ChildItem -LiteralPath $registryRoot -ErrorAction SilentlyContinue) {
            $entry = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if ($null -ne $entry -and $entry.DisplayName -eq 'Project File Hub') {
                $entry
            }
        }
    }
    return @($entries)
}

$existingInstallerEntries = @(Get-ProjectFileHubUninstallEntries)
if ($existingInstallerEntries.Count -gt 0) {
    $existingSummary = ($existingInstallerEntries | ForEach-Object {
        "$($_.DisplayVersion): $($_.InstallLocation)"
    }) -join '; '
    throw "检测到已注册的 Project File Hub 安装器，拒绝用冒烟测试覆盖：$existingSummary"
}

$testId = [Guid]::NewGuid().ToString('N')
$smokeRoot = Join-Path $verificationRoot "installer-smoke-$version-$testId"
$installRoot = Join-Path $smokeRoot 'installed'
$firstInstallLog = Join-Path $smokeRoot 'first-install.log'
$upgradeInstallLog = Join-Path $smokeRoot 'upgrade-install.log'
$uninstallLog = Join-Path $smokeRoot 'uninstall.log'
$summaryPath = Join-Path $smokeRoot 'summary.json'
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

$userDataRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ProjectFileHub'))
$userDataRootExisted = Test-Path -LiteralPath $userDataRoot -PathType Container
New-Item -ItemType Directory -Path $userDataRoot -Force | Out-Null
$userDataProbe = Join-Path $userDataRoot "installer-preserve-$testId.tmp"
Set-Content -LiteralPath $userDataProbe -Value 'Project File Hub installer user-data preservation probe.' -Encoding utf8

$desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'Project File Hub.lnk'
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'Project File Hub.lnk'
if (-not $SkipShortcuts) {
    $existingShortcuts = @(@($desktopShortcut, $startMenuShortcut) | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf
    })
    if ($existingShortcuts.Count -gt 0) {
        throw "冒烟测试不会覆盖现有快捷方式：$($existingShortcuts -join ', ')"
    }
}

function Invoke-InstallerProcess {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$Operation
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        throw "$Operation 失败，退出代码：$($process.ExitCode)"
    }
}

function Get-TestInstallEntry {
    $expectedLocation = [System.IO.Path]::GetFullPath($installRoot).TrimEnd('\')
    $matchingEntries = @(Get-ProjectFileHubUninstallEntries | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.InstallLocation) -and
        [System.IO.Path]::GetFullPath([string]$_.InstallLocation).TrimEnd('\') -eq $expectedLocation
    })
    if ($matchingEntries.Count -ne 1) {
        throw "期望一个安装/卸载注册项，实际为 $($matchingEntries.Count)：$expectedLocation"
    }
    return $matchingEntries[0]
}

function Assert-InstalledPayload {
    $requiredInstalledFiles = @(
        'app\ProjectFileHub.exe',
        'app\ProjectFileHub.dll',
        'app\App.xbf',
        'app\MainWindow.xbf',
        'app\Themes\FocusCanvas.xbf',
        'app\ProjectFileHub.pri',
        'uninstall\unins000.exe'
    )
    $missingInstalledFiles = @($requiredInstalledFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $installRoot $_) -PathType Leaf)
    })
    if ($missingInstalledFiles.Count -gt 0) {
        throw "安装结果缺少文件：$($missingInstalledFiles -join ', ')"
    }

    $installedExe = Join-Path $installRoot 'app\ProjectFileHub.exe'
    $installedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
    if ($installedVersion -ne $versionQuad) {
        throw "已安装 EXE 版本 $installedVersion 与预期 $versionQuad 不一致。"
    }

    $entry = Get-TestInstallEntry
    if ([string]$entry.DisplayVersion -ne $version -or [string]$entry.Publisher -ne 'Anjero') {
        throw "安装/卸载注册信息不正确：版本=$($entry.DisplayVersion)，发布者=$($entry.Publisher)"
    }
    if ([string]::IsNullOrWhiteSpace([string]$entry.UninstallString)) {
        throw '安装/卸载注册项缺少 UninstallString。'
    }
}

$testProcess = $null
$uninstallCompleted = $false
$launchResult = if ($SkipLaunch) { 'skipped' } else { 'pending' }

try {
    $commonInstallArguments = @(
        '/SP-',
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/NORESTARTAPPLICATIONS',
        "/DIR=`"$installRoot`""
    )
    if ($SkipShortcuts) {
        $commonInstallArguments += '/PFHSMOKETEST=1'
    }

    Invoke-InstallerProcess `
        -FilePath $installerFullPath `
        -Arguments ($commonInstallArguments + "/LOG=`"$firstInstallLog`"") `
        -Operation '首次静默安装'
    Assert-InstalledPayload

    if (-not $SkipShortcuts) {
        foreach ($shortcutPath in @($desktopShortcut, $startMenuShortcut)) {
            if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) {
                throw "安装后缺少快捷方式：$shortcutPath"
            }
        }
    }

    $staleUpgradeProbe = Join-Path $installRoot 'app\stale-upgrade-probe.tmp'
    Set-Content -LiteralPath $staleUpgradeProbe -Value 'This file must disappear during in-place upgrade.' -Encoding ascii

    Invoke-InstallerProcess `
        -FilePath $installerFullPath `
        -Arguments ($commonInstallArguments + "/LOG=`"$upgradeInstallLog`"") `
        -Operation '同 AppId 原位升级安装'
    Assert-InstalledPayload
    if (Test-Path -LiteralPath $staleUpgradeProbe) {
        throw '升级安装没有清理旧的自包含 payload 文件。'
    }

    if (-not $SkipLaunch) {
        $installedExe = Join-Path $installRoot 'app\ProjectFileHub.exe'
        $installedWorkingDirectory = Split-Path -Parent $installedExe
        $testProcess = Start-Process `
            -FilePath $installedExe `
            -WorkingDirectory $installedWorkingDirectory `
            -PassThru

        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 500
            $testProcess.Refresh()
            if ($testProcess.HasExited) {
                throw "安装后的程序提前退出，退出代码：$($testProcess.ExitCode)"
            }
        } while (($testProcess.MainWindowHandle -eq 0 -or $testProcess.MainWindowTitle -ne 'Project File Hub') -and
            [DateTime]::UtcNow -lt $deadline)

        if ($testProcess.MainWindowHandle -eq 0 -or $testProcess.MainWindowTitle -ne 'Project File Hub') {
            throw '安装后的程序未在 20 秒内创建 Project File Hub 主窗口。'
        }

        Start-Sleep -Seconds 3
        $testProcess.Refresh()
        if ($testProcess.HasExited -or -not $testProcess.Responding) {
            throw '安装后的程序创建窗口后未保持稳定响应。'
        }
        $launchResult = 'passed'

        Stop-Process -Id $testProcess.Id -Force
        [void]$testProcess.WaitForExit(5000)
        $testProcess = $null
    }

    $uninstallerPath = Join-Path $installRoot 'uninstall\unins000.exe'
    Invoke-InstallerProcess `
        -FilePath $uninstallerPath `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$uninstallLog`"") `
        -Operation '静默卸载'

    $uninstallDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while ((Test-Path -LiteralPath (Join-Path $installRoot 'app\ProjectFileHub.exe')) -and
        [DateTime]::UtcNow -lt $uninstallDeadline) {
        Start-Sleep -Milliseconds 500
    }

    if (Test-Path -LiteralPath (Join-Path $installRoot 'app\ProjectFileHub.exe')) {
        throw '卸载后应用主程序仍然存在。'
    }
    if (@(Get-ProjectFileHubUninstallEntries | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.InstallLocation) -and
            [System.IO.Path]::GetFullPath([string]$_.InstallLocation).TrimEnd('\') -eq
                [System.IO.Path]::GetFullPath($installRoot).TrimEnd('\')
        }).Count -ne 0) {
        throw '卸载后安装/卸载注册项仍然存在。'
    }
    if (-not $SkipShortcuts) {
        $remainingShortcuts = @(@($desktopShortcut, $startMenuShortcut) | Where-Object {
            Test-Path -LiteralPath $_ -PathType Leaf
        })
        if ($remainingShortcuts.Count -gt 0) {
            throw "卸载后仍残留快捷方式：$($remainingShortcuts -join ', ')"
        }
    }
    if (-not (Test-Path -LiteralPath $userDataProbe -PathType Leaf)) {
        throw '卸载错误地删除了应用数据目录中的保留探针。'
    }
    $uninstallCompleted = $true

    $summary = [ordered]@{
        version = $version
        runtime = $Runtime
        installer = $installerFullPath
        installerSha256 = (Get-FileHash -LiteralPath $installerFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        firstInstall = 'passed'
        inPlaceUpgrade = 'passed'
        installedLaunch = $launchResult
        uninstall = 'passed'
        userDataPreserved = $true
        shortcuts = if ($SkipShortcuts) { 'skipped' } else { 'passed' }
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "Installer smoke: passed"
    Write-Host "Summary:         $summaryPath"
}
finally {
    if ($null -ne $testProcess) {
        $testProcess.Refresh()
        if (-not $testProcess.HasExited) {
            Stop-Process -Id $testProcess.Id -Force
            [void]$testProcess.WaitForExit(5000)
        }
    }

    if (-not $uninstallCompleted) {
        $fallbackUninstaller = Join-Path $installRoot 'uninstall\unins000.exe'
        if (Test-Path -LiteralPath $fallbackUninstaller -PathType Leaf) {
            try {
                $cleanupProcess = Start-Process `
                    -FilePath $fallbackUninstaller `
                    -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
                    -PassThru `
                    -Wait
                if ($cleanupProcess.ExitCode -ne 0) {
                    Write-Warning "失败后的安装器清理退出代码：$($cleanupProcess.ExitCode)"
                }
            }
            catch {
                Write-Warning "失败后的安装器清理未完成：$($_.Exception.Message)"
            }
        }
    }

    if (Test-Path -LiteralPath $userDataProbe -PathType Leaf) {
        Remove-Item -LiteralPath $userDataProbe -Force
    }
    if (-not $userDataRootExisted -and
        (Test-Path -LiteralPath $userDataRoot -PathType Container) -and
        @(Get-ChildItem -LiteralPath $userDataRoot -Force).Count -eq 0) {
        Remove-Item -LiteralPath $userDataRoot -Force
    }
}
