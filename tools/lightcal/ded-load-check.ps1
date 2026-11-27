# Load one or more levels on the headless dedicated server and say whether each one starts.
# usage: powershell.exe -File ded-load-check.ps1 -Levels "Hue,rf_calib,rf_calib_safe" [-Seconds 35] [-Game <install folder>]
# The levels must already be in Mods\BfVietnam\Archives\BfVietnam\levels. Always include a level known to load.
# The server rewrites its settings files, so the whole settings folder is copied aside first and put back after,
# and the copy is compared file by file. Refuses to run while the game or a server is already up.
param([string[]]$Levels, [int]$Seconds = 35, [string]$Mode = 'GPM_CQ', [string]$Game = 'D:\Games\EA GAMES\Battlefield Vietnam')

$Levels = @($Levels | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$game = $Game
$settings = Join-Path $game 'Mods\BfVietnam\settings'
$log = Join-Path $game ('Mods\BfVietnam\Logs\BfLog_' + $env:COMPUTERNAME + '.log')
$backup = Join-Path $env:TEMP 'rf_ded_settings_backup'

$running = Get-Process | Where-Object { $_.ProcessName -match '^(bfvietnam|BfVietnam)' }
if ($running) { Write-Output ('REFUSED: running: ' + (($running | ForEach-Object ProcessName) -join ', ')); exit 2 }

if (Test-Path $backup) { Remove-Item -Recurse -Force $backup -Confirm:$false }
Copy-Item -Recurse $settings $backup
$logBackup = $null
if (Test-Path $log) { $logBackup = Join-Path $env:TEMP 'rf_ded_BfLog.before'; Copy-Item $log $logBackup -Force }

function Get-Hashes($dir) {
    $h = @{}
    Get-ChildItem -Recurse -File $dir | ForEach-Object { $h[$_.FullName.Substring($dir.Length)] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    return $h
}
$before = Get-Hashes $backup

try {
    foreach ($lvl in $Levels) {
        Set-Content -Path (Join-Path $settings 'maplist.con') -Encoding ascii -Value @("game.addLevel $lvl $Mode bfvietnam", "game.setCurrentLevel $lvl $Mode bfvietnam")
        $ss = Get-Content (Join-Path $backup 'ServerSettings.con') | ForEach-Object { if ($_ -match '^\s*game\.serverGamePlayMode\b') { "game.serverGamePlayMode $Mode" } else { $_ } }
        Set-Content -Path (Join-Path $settings 'ServerSettings.con') -Encoding ascii -Value $ss
        if (Test-Path $log) { Remove-Item $log -Force -Confirm:$false }

        $p = Start-Process -FilePath (Join-Path $game 'bfvietnam_w32ded.exe') -ArgumentList '+restart', '1' -WorkingDirectory $game -PassThru -WindowStyle Minimized
        $started = $false; $alive = $true; $t = 0
        while ($t -lt $Seconds) {
            Start-Sleep -Milliseconds 1000; $t++
            if ($p.HasExited) { $alive = $false; break }
            if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'GameStart' -Quiet)) { $started = $true; if ($t -gt 12) { break } }
        }
        $levelLine = ''
        if (Test-Path $log) { $levelLine = (Select-String -Path $log -Pattern 'Level:' | Select-Object -First 1).Line }
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -Confirm:$false; Start-Sleep -Milliseconds 800 }
        $verdict = if ($started -and $alive) { 'PASS' } elseif ($alive) { 'NO GameStart within the time' } else { "FAIL: server exited after $t s" }
        Write-Output ("{0,-16} {1}   [{2}]" -f $lvl, $verdict, $levelLine)
    }
}
finally {
    Get-Process | Where-Object { $_.ProcessName -match '^bfvietnam_w32ded' } | ForEach-Object { Stop-Process -Id $_.Id -Force -Confirm:$false }
    Start-Sleep -Milliseconds 500
    # Put the settings back exactly: remove what the server added, restore what it changed.
    Get-ChildItem -Recurse -File $settings | ForEach-Object {
        $rel = $_.FullName.Substring($settings.Length)
        if (-not $before.ContainsKey($rel)) { Remove-Item $_.FullName -Force -Confirm:$false }
    }
    Copy-Item -Recurse -Force (Join-Path $backup '*') $settings
    if ($logBackup) { Copy-Item $logBackup $log -Force }
    $after = Get-Hashes $settings
    $diff = @($before.Keys | Where-Object { $after[$_] -ne $before[$_] }) + @($after.Keys | Where-Object { -not $before.ContainsKey($_) })
    if ($diff.Count -eq 0) { Write-Output ("settings restored: {0} files identical" -f $before.Count) }
    else { Write-Output ('SETTINGS DIFFER: ' + ($diff -join ', ')) }
}
