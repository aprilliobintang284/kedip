# Builds Kedip (Release) into a fixed folder, adds a Start Menu shortcut and (re)starts it.
# Run again after changing the code to update the installed copy. Settings in %APPDATA%\Kedip are kept.
param([string]$Destination = "$env:LOCALAPPDATA\Programs\Kedip")

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Kedip\Kedip.csproj'

# The running copy locks its files; settings are saved on every change, so a hard stop loses nothing.
Get-Process Kedip -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

& $dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $Destination --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $Destination 'Kedip.exe'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Kedip.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $Destination
$link.IconLocation = "$exe,0"
$link.Description = 'Kedip - notch ringan buatan Aprillio'
$link.Save()

# Start it through the WMI process service instead of as a child of this shell: a shell inside a packaged app (such
# as an AI desktop app's terminal) would pass its private, virtualized view of AppData and HKCU on to Kedip, and its
# settings and "start with Windows" entry would never reach the real ones.
$started = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`""; CurrentDirectory = $Destination }
if ($started.ReturnValue -ne 0) { Start-Process -FilePath $exe -WorkingDirectory $Destination }
"Kedip installed to $Destination"
