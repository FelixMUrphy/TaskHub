# Packs the TaskHub MCP server (taskhub.exe mcp) as an MCP bundle for the MCP Registry:
#   dist\taskhub-mcp-win-x64.mcpb  - the bundle (release asset; the name must contain "mcp" for the registry)
#   dist\mcp-server.json           - packaging\mcp\server.json with this version, the asset URL and its SHA-256
# Needs Node.js (npx runs the pinned @anthropic-ai/mcpb CLI, which validates the manifest and packs).
# Usage: packaging\build-mcpb.ps1 [-Version 2.9.2] [-Cli app\taskhub.exe] [-Repo FelixMUrphy/TaskHub]
param(
    [string]$Version,
    [string]$Cli = "app\taskhub.exe",
    [string]$Repo = "FelixMUrphy/TaskHub",
    [string]$McpbCli = "@anthropic-ai/mcpb@2.1.2"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $Version) { $Version = [regex]::Match((Get-Content src/Host/Settings.cs -Raw), 'Version = "([^"]+)"').Groups[1].Value }
if (-not (Test-Path $Cli)) { throw "taskhub.exe not found: $Cli (build.cmd first)" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("taskhub-mcpb-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force (Join-Path $work "server") | Out-Null
try {
    $m = Get-Content packaging/mcp/manifest.json -Raw | ConvertFrom-Json
    $m.version = $Version
    [IO.File]::WriteAllText((Join-Path $work "manifest.json"), ($m | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Copy-Item $Cli (Join-Path $work "server\taskhub.exe")
    Copy-Item packaging/Assets/Square150x150Logo.png (Join-Path $work "icon.png")

    New-Item -ItemType Directory -Force dist | Out-Null
    $out = Join-Path $root "dist\taskhub-mcp-win-x64.mcpb"
    if (Test-Path $out) { Remove-Item $out }
    & npx --yes $McpbCli validate (Join-Path $work "manifest.json")
    if ($LASTEXITCODE) { throw "mcpb validate failed" }
    & npx --yes $McpbCli pack $work $out
    if ($LASTEXITCODE -or -not (Test-Path $out)) { throw "mcpb pack failed" }

    $sha = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
    $s = Get-Content packaging/mcp/server.json -Raw | ConvertFrom-Json
    if ($s.description.Length -gt 100) { throw "server.json description is longer than 100 characters" }
    $s.version = $Version
    $s.packages[0].version = $Version
    $s.packages[0].identifier = "https://github.com/$Repo/releases/download/v$Version/taskhub-mcp-win-x64.mcpb"
    $s.packages[0].fileSha256 = $sha
    [IO.File]::WriteAllText((Join-Path $root "dist\mcp-server.json"), ($s | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Output "mcpb ok: $out ($sha)"
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
