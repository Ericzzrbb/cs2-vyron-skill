<#
.SYNOPSIS
    Builds the VyronSkill plugin and copies it into a CounterStrikeSharp enabled CS2 server.

.DESCRIPTION
    Deploys the plugin the way CounterStrikeSharp expects it:
        <csgo>/addons/counterstrikesharp/plugins/VyronSkill/
            VyronSkill.dll
            VyronSkill.deps.json
            VyronSkill.pdb
            lang/en.json
            lang/zh-CN.json

.PARAMETER ServerPath
    The CS2 server "game/csgo" directory, for example D:\cs2server\game\csgo.

.PARAMETER Configuration
    Build configuration, Release by default.

.PARAMETER SkipBuild
    Copy the existing build output instead of rebuilding first.

.EXAMPLE
    pwsh ./scripts/deploy.ps1 -ServerPath "D:\cs2server\game\csgo"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,

    [string]$Configuration = 'Release',

    [switch]$SkipBuild,

    [string]$CssApiVersion = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\VyronSkill\VyronSkill.csproj'
$outputPath = Join-Path $repoRoot "src\VyronSkill\bin\$Configuration\net10.0"
$pluginTarget = Join-Path $ServerPath 'addons\counterstrikesharp\plugins\VyronSkill'

if (-not (Test-Path (Join-Path $ServerPath 'addons\counterstrikesharp'))) {
    throw "'$ServerPath' does not look like a CS2 csgo directory: addons\counterstrikesharp was not found."
}

if (-not $SkipBuild) {
    Write-Host "Building $projectPath ($Configuration)..." -ForegroundColor Cyan
    $buildArgs = @('build', $projectPath, '-c', $Configuration, '--nologo')

    if ($CssApiVersion) {
        $buildArgs += "-p:CssApiVersion=$CssApiVersion"
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path (Join-Path $outputPath 'VyronSkill.dll'))) {
    throw "Build output not found at $outputPath."
}

New-Item -ItemType Directory -Force -Path $pluginTarget | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $pluginTarget 'lang') | Out-Null

Copy-Item (Join-Path $outputPath 'VyronSkill.dll') $pluginTarget -Force
Copy-Item (Join-Path $outputPath 'VyronSkill.deps.json') $pluginTarget -Force

$pdb = Join-Path $outputPath 'VyronSkill.pdb'
if (Test-Path $pdb) {
    Copy-Item $pdb $pluginTarget -Force
}

Copy-Item (Join-Path $outputPath 'lang\*') (Join-Path $pluginTarget 'lang') -Force

Write-Host "Deployed to $pluginTarget" -ForegroundColor Green
Get-ChildItem $pluginTarget -Recurse -File | ForEach-Object { Write-Host ("  " + $_.FullName.Substring($pluginTarget.Length + 1)) }

Write-Host ''
Write-Host 'Next steps: restart the server, or use the server console:' -ForegroundColor Yellow
Write-Host '  css_plugins reload VyronSkill'
Write-Host '  css_vyron_status'
