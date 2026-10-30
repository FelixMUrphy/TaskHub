<#
Builds the Microsoft Store edition: dist\TaskHub-<version>.msix (unsigned - the Store signs it).
Run build.cmd first (it makes app\TaskHubHost.exe and obj\cli\taskhub.exe).

  powershell -File packaging\build-msix.ps1                       # pack (needs makeappx.exe from the Windows SDK,
                                                                  # or -MakeAppx PATH, e.g. from Microsoft.Windows.SDK.BuildTools)
  powershell -File packaging\build-msix.ps1 -Register             # dev: register the folder as an installed package
                                                                  # (Developer Mode; replaces an earlier registration)
  powershell -File packaging\build-msix.ps1 -Unregister           # dev: remove that registration again

Identity: -Name / -Publisher / -PublisherDisplay from Partner Center (the app → Product identity); the defaults are
placeholders for local tests and must not be uploaded.
#>
param(
    [string]$Name = "TaskHub.Dev",
    [string]$Publisher = "CN=TaskHubDev",
    [string]$PublisherDisplay = "TaskHub",
    [string]$MakeAppx,                  # path to makeappx.exe if the Windows SDK is not installed
    [switch]$Register,
    [switch]$Unregister
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if ($Register -or $Unregister) {
    Get-AppxPackage -Name $Name | Remove-AppxPackage
    if ($Unregister) { "unregistered $Name"; return }
}

$version = [regex]::Match((Get-Content "$root\src\Host\Settings.cs" -Raw), 'Version = "([^"]+)"').Groups[1].Value
$hub = "$root\app\TaskHubHost.exe"; $cli = "$root\obj\cli\taskhub.exe"
foreach ($f in $hub, $cli) { if (-not (Test-Path $f)) { throw "$f not found - run build.cmd first" } }

$layout = "$root\obj\msix\layout"
if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
New-Item -ItemType Directory -Force "$layout\Assets" | Out-Null
Copy-Item $hub, $cli, "$root\API.md", "$root\clients\python\taskhub_client.py" $layout
Copy-Item "$PSScriptRoot\Assets\*.png" "$layout\Assets"

$x = [Security.SecurityElement]
$manifest = (Get-Content "$PSScriptRoot\AppxManifest.xml" -Raw -Encoding UTF8).
    Replace('$NAME$', $x::Escape($Name)).Replace('$PUBLISHER$', $x::Escape($Publisher)).
    Replace('$PUBLISHER_DISPLAY$', $x::Escape($PublisherDisplay)).Replace('$VERSION$', "$version.0")   # the Store wants x.y.z.0
[IO.File]::WriteAllText("$layout\AppxManifest.xml", $manifest, (New-Object Text.UTF8Encoding $false))

if ($Register) {
    Add-AppxPackage -Register "$layout\AppxManifest.xml"
    "registered $Name $version from $layout"
    return
}

if (-not $MakeAppx) {
    $MakeAppx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $MakeAppx) { throw "makeappx.exe not found - install the Windows SDK or pass -MakeAppx" }
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$out = "$root\dist\TaskHub-$version.msix"
& $MakeAppx pack /d $layout /p $out /o /h SHA256 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }
"msix ok: $out"
