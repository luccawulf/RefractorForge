# Load one or more Battlefield 1942 levels on the headless dedicated server and say whether each one starts.
# usage: powershell.exe -File ded-load-check-bf1942.ps1 -Levels "rf_calib42,Snow_El_Alamein" [-Seconds 60]
# BF1942's server writes no log, so "started" is read from its GameSpy status reply (UDP, \status\): a server that
# has a level up answers with its mapname. Always include a level known to load. The settings folder is copied
# aside first and put back after, compared file by file. Refuses to run while the game or a server is already up.
param([string[]]$Levels, [int]$Seconds = 60, [string]$Mode = 'GPM_CQ', [string]$Game = 'D:\Games\EA GAMES\Battlefield 1942')

$Levels = @($Levels | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$settings = Join-Path $Game 'Mods\bf1942\Settings'
$backup = Join-Path $env:TEMP 'rf_ded42_settings_backup'

$running = Get-Process | Where-Object { $_.ProcessName -match '^(BF1942|bf42)' }
if ($running) { Write-Output ('REFUSED: running: ' + (($running | ForEach-Object ProcessName) -join ', ')); exit 2 }

if (Test-Path $backup) { Remove-Item -Recurse -Force $backup -Confirm:$false }
Copy-Item -Recurse $settings $backup

function Get-Hashes($dir) {
    $h = @{}
    Get-ChildItem -Recurse -File $dir | ForEach-Object { $h[$_.FullName.Substring($dir.Length)] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    return $h
}
$before = Get-Hashes $backup

function Get-Status([int]$port) {
    $u = New-Object System.Net.Sockets.UdpClient
    try {
        $u.Client.ReceiveTimeout = 700
        $q = [System.Text.Encoding]::ASCII.GetBytes('\status\')
        [void]$u.Send($q, $q.Length, '127.0.0.1', $port)
        $ep = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any, 0)
        $text = ''
        for ($i = 0; $i -lt 4; $i++) {
            try { $text += [System.Text.Encoding]::ASCII.GetString($u.Receive([ref]$ep)) } catch { break }
            if ($text -match '\\final\\') { break }
        }
        return $text
    } catch { return '' } finally { $u.Close() }
}

try {
    foreach ($lvl in $Levels) {
        Set-Content -Path (Join-Path $settings 'maplist.con') -Encoding ascii -Value @("game.addLevel $lvl $Mode bf1942", "game.setCurrentLevel $lvl $Mode bf1942")
        $p = Start-Process -FilePath (Join-Path $Game 'BF1942_w32ded.exe') -ArgumentList '+game', 'bf1942', '+statusMonitor', '0' -WorkingDirectory $Game -PassThru -WindowStyle Minimized
        $map = ''; $mode = ''; $alive = $true; $t = 0; $peak = 0
        while ($t -lt $Seconds) {
            Start-Sleep -Milliseconds 1000; $t++
            if ($p.HasExited) { $alive = $false; break }
            try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
            foreach ($port in 23000, 22000) {
                $s = Get-Status $port
                if ($s -match '\\mapname\\([^\\]*)') { $map = $Matches[1] }
                if ($s -match '\\gametype\\([^\\]*)') { $mode = $Matches[1] }
                if ($map) { break }
            }
            if ($map -and $t -gt 8) { break }
        }
        $code = ''
        $ports = ''; $title = ''; $cpu = 0
        if (-not $p.HasExited) {
            try { $ports = ((Get-NetUDPEndpoint -OwningProcess $p.Id -ErrorAction SilentlyContinue | ForEach-Object LocalPort | Sort-Object -Unique) -join ',') } catch { }
            try {
                $p.Refresh()
                if ($p.MainWindowTitle -and $p.MainWindowTitle -notmatch 'Dedicated Server') {
                    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
                    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
                    $tops = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
                    $parts = @()
                    foreach ($top in $tops) {
                        $parts += ('[' + $top.Current.Name + ']')
                        $all = $top.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
                        foreach ($d in $all) {
                            $n = $d.Current.Name
                            $tp = $null
                            if ($d.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$tp)) {
                                $txt = $tp.DocumentRange.GetText(4000).Trim()
                                if ($txt) { $n = 'console: ' + (($txt -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join ' / ') }
                            }
                            if ($n -and $n -notmatch '^(OK|Close|Abort|Retry|Ignore|Minimize|Maximize|System|Text Area)$') { $parts += $n }
                        }
                    }
                    $errText = $parts -join ' | '
                }
            } catch { $errText = 'could not read the box: ' + $_.Exception.Message }
            try { $p.Refresh(); $title = $p.MainWindowTitle; $cpu = [math]::Round($p.TotalProcessorTime.TotalSeconds, 1) } catch { }
        }
        if ($p.HasExited) { $code = " (exit code $($p.ExitCode))" } else { Stop-Process -Id $p.Id -Force -Confirm:$false; Start-Sleep -Milliseconds 800 }
        $verdict = if (($map -or $ports -match '14567') -and $alive) { 'PASS (level up: game port open)' } elseif ($errText) { 'FAIL: error box' } elseif ($alive) { "NO status reply within $Seconds s (still running)" } else { "FAIL: server exited after $t s$code" }
        Start-Sleep -Seconds 5   # let the ports go before the next server starts
        Write-Output ("{0,-18} {1}   [mapname '{2}' gametype '{3}', peak {4:N0} MB, udp ports '{5}', window '{6}', cpu {7} s]" -f $lvl, $verdict, $map, $mode, ($peak / 1MB), $ports, $title, $cpu)
        if ($errText) { Write-Output ('    error box: ' + $errText) }; $errText = ''
    }
}
finally {
    Get-Process | Where-Object { $_.ProcessName -match '^BF1942_w32ded' } | ForEach-Object { Stop-Process -Id $_.Id -Force -Confirm:$false }
    Start-Sleep -Milliseconds 500
    Get-ChildItem -Recurse -File $settings | ForEach-Object {
        $rel = $_.FullName.Substring($settings.Length)
        if (-not $before.ContainsKey($rel)) { Remove-Item $_.FullName -Force -Confirm:$false }
    }
    Copy-Item -Recurse -Force (Join-Path $backup '*') $settings
    $after = Get-Hashes $settings
    $diff = @($before.Keys | Where-Object { $after[$_] -ne $before[$_] }) + @($after.Keys | Where-Object { -not $before.ContainsKey($_) })
    if ($diff.Count -eq 0) { Write-Output ("settings restored: {0} files identical" -f $before.Count) }
    else { Write-Output ('SETTINGS DIFFER: ' + ($diff -join ', ')) }
}
