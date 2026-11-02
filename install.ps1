# PremiereFX: build + install.
#  1. Compiles the Ctrl+Space helper (helper\PremiereFX.cs) with the .NET Framework compiler.
#  2. Packs plugin\ into dist\PremiereFX.ccx and installs it with Adobe's UnifiedPluginInstallerAgent.
#  3. Copies the helper to %LOCALAPPDATA%\PremiereFX, adds it to Windows startup, and starts it.
# Run:  powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

# 1. helper
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Get-Process PremiereFX -ErrorAction SilentlyContinue | Stop-Process -Force
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /out:"$dist\PremiereFX.exe" `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "$root\helper\PremiereFX.cs"
if ($LASTEXITCODE -ne 0) { throw "Helper build failed" }

# 2. plugin (.ccx is a zip with manifest.json at the root)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ccx = Join-Path $dist "PremiereFX.ccx"
if (Test-Path $ccx) { Remove-Item $ccx }
[System.IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $root "plugin"), $ccx)
$upia = "C:\Program Files\Common Files\Adobe\Adobe Desktop Common\RemoteComponents\UPI\UnifiedPluginInstallerAgent\UnifiedPluginInstallerAgent.exe"
& $upia /install $ccx
Write-Host "UPIA exit code: $LASTEXITCODE"

# 3. helper to LocalAppData + startup shortcut
$appDir = Join-Path $env:LOCALAPPDATA "PremiereFX"
New-Item -ItemType Directory -Force $appDir | Out-Null
Copy-Item "$dist\PremiereFX.exe" $appDir -Force
$exe = Join-Path $appDir "PremiereFX.exe"
$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "PremiereFX.lnk"
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $appDir
$s.Description = "PremiereFX for Premiere Pro (Ctrl+Space)"
$s.Save()
Start-Process $exe
Write-Host "Installed. Helper: $exe"
