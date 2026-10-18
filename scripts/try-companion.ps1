<#
.SYNOPSIS
Builds the Modbot Companion from this checkout and starts it, so you can try a change on your own PC.

.DESCRIPTION
Builds only src\Modbot.Companion.App (Release) into artifacts\try-companion\<time>\, which git
ignores. Nothing is committed, uploaded or released. Run it from any checkout or worktree.

The Companion allows one copy per Windows account, so a running Modbot is closed first, after you
answer y. The test build uses the same settings and pairings as the installed copy
(%APPDATA%\Modbot). The script itself changes nothing in Windows, and a copy run from a plain
folder never checks for updates or touches start-with-Windows. It does point the modbot-companion://
link at itself, as every start of the app does (UrlSchemeRegistration); starting the installed
Modbot again points it back.

To go back: quit the test build and start Modbot from the Start menu as usual.

.PARAMETER BuildOnly
Build and print where the exe is. Does not close or start anything.
#>
[CmdletBinding()]
param(
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Modbot.Companion.App\Modbot.Companion.App.csproj'
$testRoot = Join-Path $repo 'artifacts\try-companion'
$out = Join-Path $testRoot (Get-Date -Format 'yyyyMMdd-HHmmss')

Write-Host "Building the Companion from $repo ..."
Push-Location $repo
try {
    & dotnet build $project -c Release -o $out --nologo -v:minimal
    $buildExit = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($buildExit -ne 0) {
    throw "The build failed (dotnet exit code $buildExit). Nothing was closed or started."
}

$exe = Join-Path $out 'Modbot.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "The build finished but $exe is not there."
}

if ($BuildOnly) {
    Write-Host "Built: $exe"
    return
}

# The running Modbot (the installed one, or an earlier test build) owns the tray icon, so it has
# to go. The app has no way to be told to quit from outside (its window's X only hides it to the
# tray), so after the y answer the process is stopped.
#
# Only this Windows session's copies count: the one-copy lock is per session (Local\), so another
# signed-in account's Modbot is neither in the way nor ours to stop.
$session = (Get-Process -Id $PID).SessionId
$running = @(Get-Process -Name 'Modbot' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
if ($running.Count -gt 0) {
    $answer = Read-Host 'Close the running Modbot and start the test build? (y/n)'
    if ($answer -notmatch '^\s*y') {
        Write-Host "Left Modbot running. The test build is at: $exe"
        return
    }
    foreach ($process in $running) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            [void]$process.WaitForExit(10000)
        }
        catch {
            Write-Host "Could not stop Modbot (process $($process.Id)): $($_.Exception.Message)"
        }
    }

    $stillRunning = @(Get-Process -Name 'Modbot' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
    if ($stillRunning.Count -gt 0) {
        $ids = ($stillRunning | ForEach-Object { $_.Id }) -join ', '
        throw "Modbot is still running (process $ids), so the test build was not started. The test build is at: $exe"
    }
}

# Earlier test builds are no use now; one still in use is left alone.
Get-ChildItem -LiteralPath $testRoot -Directory |
    Where-Object { $_.FullName -ne $out } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

# A leftover MODBOT_DATA_FOLDER would make the test build a separate "test copy" with its own
# settings and pairings. It is cleared for the started copy only and put back afterwards.
$dataFolderVariable = 'MODBOT_DATA_FOLDER'
$savedDataFolder = [Environment]::GetEnvironmentVariable($dataFolderVariable)
if ($savedDataFolder) {
    Write-Host "Warning: $dataFolderVariable is set; the test build ignores it and uses the normal settings and pairings."
    [Environment]::SetEnvironmentVariable($dataFolderVariable, $null)
}
try {
    Start-Process -FilePath $exe -WorkingDirectory $out
}
finally {
    if ($savedDataFolder) {
        [Environment]::SetEnvironmentVariable($dataFolderVariable, $savedDataFolder)
    }
}
Write-Host "Started the test build: $exe"
Write-Host 'To go back: quit the test build (tray icon, Quit) and start Modbot from the Start menu as usual.'
