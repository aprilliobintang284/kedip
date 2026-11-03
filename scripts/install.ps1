# Builds Kedip (Release) into a fixed folder, adds a Start Menu shortcut and (re)starts it.
# Run again after changing the code to update the installed copy. Settings in %APPDATA%\Kedip are kept.
param([string]$Destination = "$env:LOCALAPPDATA\Programs\Kedip")

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Kedip\Kedip.csproj'

Add-Type -Name Taskbar -Namespace KedipInstall -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
'@

# Puts the Windows taskbar(s) back on screen ("Gantikan taskbar" hides them while Kedip runs).
function Show-Taskbar {
    foreach ($class in 'Shell_TrayWnd', 'Shell_SecondaryTrayWnd') {
        $hwnd = [IntPtr]::Zero
        while (($hwnd = [KedipInstall.Taskbar]::FindWindowEx([IntPtr]::Zero, $hwnd, $class, [NullString]::Value)) -ne [IntPtr]::Zero) {
            [void][KedipInstall.Taskbar]::ShowWindow($hwnd, 8)
        }
    }
}

# The running copy locks its files; settings are saved on every change, so a hard stop loses nothing. A hard stop
# skips Kedip's own clean-up, so the taskbar is brought back here (the new copy hides it again if it should).
Get-Process Kedip -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Show-Taskbar

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

# Windows Smart App Control may refuse a freshly built (unsigned) copy; say so instead of failing silently.
Start-Sleep -Seconds 4
if (-not (Get-Process Kedip -ErrorAction SilentlyContinue)) {
    Show-Taskbar
    $blocked = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-CodeIntegrity/Operational'; Id = 3077; StartTime = (Get-Date).AddMinutes(-1) } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Kedip' }
    if ($blocked) { throw 'Kedip did not start: Windows Smart App Control blocked this build. Change the code (or the version) and install again.' }
    throw 'Kedip did not start; see %APPDATA%\Kedip\kedip.log and the Application event log.'
}
"Kedip installed to $Destination"
