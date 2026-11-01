#Requires -Version 5.1
<#
    打包脚本：框架依赖发布 + 压缩包 + Inno Setup 安装包。

    默认发布为“框架依赖”版本（体积小），安装包里带 install-dotnet.cmd，
    首次安装时会自动检查并安装 .NET 10 桌面运行时。

    用法：
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -SelfContained
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "dist",
    [switch]$SelfContained,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

function Find-InnoCompiler {
    $candidates = @()

    # 先读注册表，允许装在任意盘符
    $uninstallKeys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
    )

    foreach ($key in $uninstallKeys) {
        if (-not (Test-Path $key)) { continue }
        $location = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstallLocation
        if ($location) { $candidates += (Join-Path $location "ISCC.exe") }
    }

    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    }
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    }

    # 装在非系统盘（本机就是 D 盘）
    $candidates += "D:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    $candidates += "D:\Program Files\Inno Setup 6\ISCC.exe"

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    return $null
}

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "ATTM.csproj"
$distRoot = Join-Path $root $Output

$version = "0.0.0"
$match = [regex]::Match((Get-Content $project -Raw), "<Version>([^<]+)</Version>")
if ($match.Success) { $version = $match.Groups[1].Value }

$flavor = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
$appDir = Join-Path $distRoot "app"
$name = "ATTM-$version-$Runtime"

Write-Host ""
Write-Host "ATTM $version ($flavor, $Runtime)" -ForegroundColor Cyan
Write-Host "  项目：$project"
Write-Host "  输出：$appDir"
if (-not $SelfContained) {
    Write-Host "  提示：框架依赖版本需要 .NET 10 桌面运行时，安装包会自动装。" -ForegroundColor DarkGray
}
Write-Host ""

if (Test-Path $appDir) {
    Write-Host "清理旧的发布目录..." -ForegroundColor DarkGray
    Remove-Item $appDir -Recurse -Force
}
New-Item -ItemType Directory -Path $appDir -Force | Out-Null

$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $appDir,
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-p:SatelliteResourceLanguages=zh-Hans",
    "-p:PublishSingleFile=true"
)

if ($SelfContained) {
    $publishArgs += @(
        "--self-contained", "true",
        "-p:IncludeNativeLibrariesForSelfExtract=false",
        "-p:EnableCompressionInSingleFile=false"
    )
} else {
    $publishArgs += @("--self-contained", "false")
}

Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# 附带文档、示例和 .NET 运行时安装助手
$docs = @(
    "README.md",
    "ATTM 语法教程.md",
    "LICENSING.md",
    "THIRD-PARTY-NOTICES.md",
    "AI-GENERATED-NOTICE.md",
    "LICENSE"
)

foreach ($file in $docs) {
    $source = Join-Path $root $file
    if (Test-Path $source) { Copy-Item $source $appDir -Force }
}

foreach ($file in @("install-dotnet.cmd", "install-dotnet.ps1")) {
    $source = Join-Path $root (Join-Path "assets" $file)
    if (Test-Path $source) { Copy-Item $source $appDir -Force }
}

$samples = Join-Path $root "samples"
if (Test-Path $samples) {
    Copy-Item $samples (Join-Path $appDir "samples") -Recurse -Force
}

# 插件：单独构建后放进 app\plugins（要在打包之前完成，否则 zip 里没有插件）
$pluginProject = Join-Path $root "plugins\ATTM.Plugin.MidiImport\ATTM.Plugin.MidiImport.csproj"
if (Test-Path $pluginProject) {
    Write-Host ""
    Write-Host "构建插件..." -ForegroundColor Cyan

    & dotnet build $pluginProject -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "插件构建失败（退出码 $LASTEXITCODE）" }

    $pluginSrc = Join-Path $root "plugins\ATTM.Plugin.MidiImport\bin\$Configuration\net10.0-windows\ATTM.Plugin.MidiImport.dll"
    if (Test-Path $pluginSrc) {
        $pluginDir = Join-Path $appDir "plugins"
        New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
        Copy-Item $pluginSrc $pluginDir -Force
        Write-Host "  plugins\ATTM.Plugin.MidiImport.dll" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  没找到插件输出，跳过" -ForegroundColor Yellow
    }
}

# 构建 plugins\ 下所有插件工程，然后把主程序输出目录里的 plugins\* 整体同步过去
$pluginProjects = Get-ChildItem (Join-Path $root "plugins") -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName ($_.Name + ".csproj") } |
    Where-Object { Test-Path $_ }

foreach ($project in $pluginProjects) {
    Write-Host ("  构建 " + (Split-Path $project -Leaf)) -ForegroundColor DarkGray
    & dotnet build $project -c $Configuration -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "插件构建失败：$project" }
}

$pluginOut = Join-Path $root "bin\$Configuration\net10.0-windows\plugins"
if (Test-Path $pluginOut) {
    $pluginDir = Join-Path $appDir "plugins"
    New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    Copy-Item (Join-Path $pluginOut "*") $pluginDir -Recurse -Force
    Write-Host "  plugins\ 已同步（含依赖与原生库）" -ForegroundColor DarkGray
}

# 压缩包
$zipPath = Join-Path $distRoot "$name.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host ""
Write-Host "打包 zip：$zipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$exe = Join-Path $appDir "ATTM.exe"
if (Test-Path $exe) {
    $exeSize = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Host "  ATTM.exe  $exeSize MB"
}

# Inno Setup
if (-not $SkipInstaller) {
    $iscc = Find-InnoCompiler

    if ($iscc) {
        Write-Host ""
        Write-Host "生成安装包（$iscc）..." -ForegroundColor Cyan

        $iss = Join-Path $root "packaging\ATTM.iss"
        $installerDir = Join-Path $distRoot "installer"

        & $iscc "/DAppVersion=$version" "/DSourceDir=$appDir" "/DOutputDir=$installerDir" $iss
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）" }

        $setup = Join-Path $installerDir "ATTM-$version-setup.exe"
        if (Test-Path $setup) {
            $setupSize = [math]::Round((Get-Item $setup).Length / 1MB, 1)
            Write-Host "  $setup  $setupSize MB"
        }
    }
    else {
        Write-Host ""
        Write-Host "没找到 Inno Setup（ISCC.exe），跳过安装包。" -ForegroundColor Yellow
        Write-Host "装好 Inno Setup 6 后重新运行本脚本即可：" -ForegroundColor Yellow
        Write-Host "  https://jrsoftware.org/isdl.php"
        Write-Host "  装在其他盘也可以，脚本会从注册表里读安装位置。"
    }
}

Write-Host ""
Write-Host "完成。" -ForegroundColor Green
