<#
.SYNOPSIS
  Builds 808 HD (libnrsc5.dll + the SDR# plugin) and packages a release zip.

.DESCRIPTION
  1. Builds libnrsc5.dll from the nrsc5 submodule with MSYS2 (skipped if already built, unless -Clean).
  2. Builds the plugin against the SDR# plugin SDK reference assemblies.
  3. Packages dist\808HD-v<version>.zip containing an "808HD" folder to drop into SDR#'s Plugins folder.

.PARAMETER SdkLibDir
  Folder containing SDRSharp.Common.dll and SDRSharp.Radio.dll from Airspy's plugin SDK
  (https://airspy.com/download -> "SDR# SDK for Plugin Developers"). Default: sdk\sdrplugins\lib

.PARAMETER SdrSharpDir
  Optional SDR# folder. If given, the built plugin is also copied to <SdrSharpDir>\Plugins\808HD.

.PARAMETER Msys2
  MSYS2 install folder. Default: C:\msys64

.EXAMPLE
  .\build.ps1
.EXAMPLE
  .\build.ps1 -SdrSharpDir C:\sdrsharp-x64
#>
param(
    [string]$SdkLibDir = "$PSScriptRoot\sdk\sdrplugins\lib",
    [string]$SdrSharpDir = "",
    [string]$Msys2 = "C:\msys64",
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = "$root\plugin\SDRSharp.HDRadio\SDRSharp.HDRadio.csproj"
$dll = "$root\nrsc5\build\src\libnrsc5.dll"

function Step($msg) { Write-Host "`n=== $msg" -ForegroundColor Cyan }

# ---- prerequisites ----
Step "Checking prerequisites"
if (-not (Test-Path "$root\nrsc5\CMakeLists.txt")) { throw "nrsc5 submodule missing. Run: git submodule update --init" }
if (-not (Test-Path "$SdkLibDir\SDRSharp.Common.dll")) {
    throw "SDR# plugin SDK not found in '$SdkLibDir'. Download 'SDR# SDK for Plugin Developers' from https://airspy.com/download, unzip it to '$root\sdk', or pass -SdkLibDir."
}
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { throw ".NET SDK not found. Install the .NET 9 SDK: winget install Microsoft.DotNet.SDK.9" }
if (-not ((& $dotnet --list-sdks) -match '^9\.')) { throw ".NET 9 SDK not found. Install it: winget install Microsoft.DotNet.SDK.9" }

# ---- libnrsc5.dll ----
if ($Clean -and (Test-Path "$root\nrsc5\build")) { Remove-Item "$root\nrsc5\build" -Recurse -Force }
if (-not (Test-Path $dll)) {
    Step "Building libnrsc5.dll with MSYS2 (first build takes several minutes)"
    $bash = "$Msys2\usr\bin\bash.exe"
    if (-not (Test-Path $bash)) { throw "MSYS2 not found at $Msys2. Install it: winget install MSYS2.MSYS2" }
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'
    & $bash -lc 'pacman -S --needed --noconfirm autoconf automake git make patch ${MINGW_PACKAGE_PREFIX}-gcc ${MINGW_PACKAGE_PREFIX}-cmake ${MINGW_PACKAGE_PREFIX}-libtool'
    if ($LASTEXITCODE) { throw "MSYS2 package install failed" }
    $script = '/' + $root.Substring(0, 1).ToLower() + ($root.Substring(2) -replace '\\', '/')   # C:\x\y -> /c/x/y
    & $bash -lc "'$script/build-nrsc5.sh'"
    if ($LASTEXITCODE -or -not (Test-Path $dll)) { throw "nrsc5 build failed" }
} else {
    Step "libnrsc5.dll already built (use -Clean to rebuild)"
}

# ---- plugin ----
Step "Building the plugin"
$props = @("-c", "Release", "-p:Platform=x64", "-p:SdkLibDir=$SdkLibDir\", "-nologo", "-v", "m")
if ($SdrSharpDir) { $props += "-p:SdrSharpDir=$SdrSharpDir\" } else { $props += "-p:SdrSharpDir=$root\__no_deploy__\" }
& $dotnet build $proj @props
if ($LASTEXITCODE) { throw "plugin build failed" }

# ---- package ----
[xml]$csproj = Get-Content $proj
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = "$root\plugin\SDRSharp.HDRadio\bin\x64\Release\net9.0-windows"
$stage = "$root\dist\stage\808HD"
$zip = "$root\dist\808HD-v$version.zip"
Step "Packaging $zip"
Remove-Item "$root\dist\stage" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$out\SDRSharp.HDRadio.dll", "$out\libnrsc5.dll" $stage
Copy-Item "$root\LICENSE" "$stage\LICENSE.txt"
Copy-Item "$root\THIRD_PARTY_NOTICES.md" $stage
Copy-Item "$root\packaging\INSTALL.txt" $stage
Remove-Item $zip -ErrorAction SilentlyContinue
# Build the zip by hand: Compress-Archive in Windows PowerShell 5.1 writes "808HD\file" entry
# names with backslashes, which non-Windows unzip tools mishandle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -File) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "808HD/$($file.Name)", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Remove-Item "$root\dist\stage" -Recurse -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
Write-Host "`nDone: $zip`nSHA256: $hash" -ForegroundColor Green
