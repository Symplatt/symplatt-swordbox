param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectPath = Join-Path $PSScriptRoot 'SwordBox.csproj'
$version = ([xml](Get-Content -LiteralPath $projectPath -Raw)).Project.PropertyGroup.Version
$releasePath = Join-Path $PSScriptRoot "dist\SwordBox-$version"
& $Dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $releasePath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $releasePath '使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '恢复桌面图标.cmd') -Destination $releasePath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '移除桌面右键入口.cmd') -Destination $releasePath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '注册桌面入口.cmd') -Destination $releasePath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination $releasePath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets') -Destination $releasePath -Recurse -Force
$zipPath = Join-Path $PSScriptRoot "dist\辛普莱特的万剑匣-$version-win-x64.zip"
Compress-Archive -LiteralPath $releasePath -DestinationPath $zipPath -Force
Write-Output $zipPath
