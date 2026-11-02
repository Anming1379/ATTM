param(
    [switch]$Quiet
)

# ============================================================
#  ATTM - .NET 10 桌面运行时安装助手
#  用法：install-dotnet.cmd        双击/普通运行（结束后暂停）
#        install-dotnet.cmd /quiet 静默运行（不暂停）
#
#  安装程序调用时不要隐藏窗口：下载 60 MB 期间要能看到进度，
#  需要管理员权限时也能看到"请在 UAC 窗口点是"的提示。
# ============================================================

$ErrorActionPreference = 'Stop'

# 与控制台编码保持一致，避免中文乱码
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$DownloadUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
$InstallerPath = Join-Path $env:TEMP 'windowsdesktop-runtime-10-win-x64.exe'
$ManualUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0'

# 下载最多 30 分钟，安装最多 20 分钟，超时就放弃而不是无限挂着
$DownloadTimeoutSeconds = 1800
$InstallTimeoutSeconds = 1200

function Write-Title {
    Write-Host ''
    Write-Host '==========================================================' -ForegroundColor DarkCyan
    Write-Host '   ATTM - ALambda Text To Midi' -ForegroundColor Cyan
    Write-Host '   .NET 10 桌面运行时 安装助手' -ForegroundColor Cyan
    Write-Host '==========================================================' -ForegroundColor DarkCyan
    Write-Host ''
}

function Test-DotNet10Installed {
    $roots = @(
        (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'),
        (Join-Path ${env:ProgramFiles(x86)} 'dotnet\shared\Microsoft.WindowsDesktop.App')
    )

    foreach ($sharedRoot in $roots) {
        if (-not $sharedRoot -or -not (Test-Path $sharedRoot)) { continue }
        $found = Get-ChildItem $sharedRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '10.*' }
        if ($found) { return $true }
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $runtimes = & dotnet --list-runtimes 2>$null
        if ($runtimes -match 'Microsoft\.WindowsDesktop\.App 10\.') { return $true }
    }

    return $false
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-BeforeExit {
    if ($Quiet -or -not [Environment]::UserInteractive) {
        return
    }

    try {
        Write-Host ''
        Write-Host '按任意键关闭窗口...' -ForegroundColor DarkGray
        [void][Console]::ReadKey($true)
    }
    catch {
        # 无控制台（例如被其他程序调用）时直接返回
    }
}

function Format-Megabytes([long]$bytes) {
    return ('{0:N1} MB' -f ($bytes / 1MB))
}

# ------------------------------------------------------------
#  下载
# ------------------------------------------------------------
function Get-RuntimeInstaller {
    if (Test-Path $InstallerPath) { Remove-Item $InstallerPath -Force -ErrorAction SilentlyContinue }

    # 优先用系统自带的 curl（能显示进度条、支持超时和重试）
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    if (Test-Path $curl) {
        Write-Host '正在下载，进度条如下（约 60 MB）...' -ForegroundColor Cyan
        Write-Host ''
        & $curl --location --fail --retry 2 --retry-delay 3 `
            --connect-timeout 30 --max-time $DownloadTimeoutSeconds `
            --progress-bar -o $InstallerPath $DownloadUrl
        if ($LASTEXITCODE -eq 0 -and (Test-Path $InstallerPath)) { return $true }
    }

    # 退路：PowerShell 自己的下载
    Write-Host 'curl 下载失败，改用 PowerShell 下载...' -ForegroundColor Yellow
    try {
        $oldProgress = $ProgressPreference
        $ProgressPreference = 'Continue'
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $InstallerPath -UseBasicParsing -TimeoutSec $DownloadTimeoutSeconds
        $ProgressPreference = $oldProgress
    }
    catch {
        $ProgressPreference = 'SilentlyContinue'
        return $false
    }

    return (Test-Path $InstallerPath)
}

# ------------------------------------------------------------
#  安装：带超时，卡住就杀掉而不是一直挂着
# ------------------------------------------------------------
function Install-Runtime {
    $process = Start-Process -FilePath $InstallerPath `
        -ArgumentList '/install', '/quiet', '/norestart' `
        -PassThru

    if (-not $process.WaitForExit($InstallTimeoutSeconds * 1000)) {
        Write-Host ''
        Write-Host '[!] 安装超时，正在结束安装进程...' -ForegroundColor Red
        try { $process.Kill() } catch { }
        return -1
    }

    return $process.ExitCode
}

try {
    Write-Title

    if (Test-DotNet10Installed) {
        Write-Host '[√] 已经安装 .NET 10 桌面运行时，无需重复安装。' -ForegroundColor Green
        Write-Host '    现在可以直接运行 ATTM.exe 了。'
        Wait-BeforeExit
        exit 0
    }

    Write-Host '未检测到 .NET 10 桌面运行时：ATTM 需要它才能启动。' -ForegroundColor Yellow
    Write-Host '接下来会自动下载并安装，全程无需操作（需要联网）。'
    Write-Host ''

    # ---- 管理员权限 ----
    if (-not (Test-IsAdmin)) {
        Write-Host '正在请求管理员权限：请在弹出的"用户账户控制"窗口里点「是」。' -ForegroundColor Yellow
        Write-Host '（如果没看到弹窗，看一下任务栏有没有闪烁的盾牌图标）'
        Write-Host ''

        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
        $arguments += '-Quiet'

        try {
            # 关键：等提权后的子进程结束，这样安装程序才知道什么时候装完
            $elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -PassThru
        }
        catch {
            Write-Host '[!] 未能获取管理员权限（UAC 被取消）。' -ForegroundColor Red
            Write-Host '    请右键点击 install-dotnet.cmd，选择「以管理员身份运行」。'
            Start-Process $ManualUrl
            Wait-BeforeExit
            exit 1
        }

        # 子进程带 -Quiet，安装过程在它自己的窗口里进行，这里只等结果
        $elevated.WaitForExit()

        Write-Host ''
        if (Test-DotNet10Installed) {
            Write-Host '[√] .NET 10 桌面运行时安装成功！' -ForegroundColor Green
            Write-Host '    现在可以直接运行 ATTM.exe 了。'
            Wait-BeforeExit
            exit 0
        }

        Write-Host '[!] 提权后的安装没有完成。' -ForegroundColor Red
        Write-Host "    请手动安装：$ManualUrl"
        Start-Process $ManualUrl
        Wait-BeforeExit
        exit 1
    }

    # ---- 下载 ----
    if (-not (Get-RuntimeInstaller)) {
        Write-Host ''
        Write-Host '[!] 下载失败或超时，可能是网络问题。' -ForegroundColor Red
        Write-Host '    即将打开微软官方下载页面，请手动下载后双击安装。'
        Start-Process $ManualUrl
        Wait-BeforeExit
        exit 1
    }

    $size = (Get-Item $InstallerPath).Length
    Write-Host ''
    Write-Host "[√] 下载完成（$(Format-Megabytes $size)）" -ForegroundColor Green
    Write-Host ''

    # ---- 安装 ----
    Write-Host '正在安装（大约 1-3 分钟，请勿关闭窗口）...' -ForegroundColor Cyan
    Write-Host ''

    $code = Install-Runtime

    Write-Host ''
    if (Test-DotNet10Installed) {
        Write-Host '[√] .NET 10 桌面运行时安装成功！' -ForegroundColor Green
        Write-Host '    现在可以直接运行 ATTM.exe 了。'
        Remove-Item $InstallerPath -Force -ErrorAction SilentlyContinue
        Wait-BeforeExit
        exit 0
    }

    Write-Host "[!] 安装未能完成（安装程序返回码 $code）。" -ForegroundColor Red
    Write-Host "    请尝试手动安装：$ManualUrl"
    Start-Process $ManualUrl
    Wait-BeforeExit
    exit 1
}
catch {
    Write-Host ''
    Write-Host "[!] 出现异常：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "    请手动安装：$ManualUrl"
    Start-Process $ManualUrl
    Wait-BeforeExit
    exit 1
}
