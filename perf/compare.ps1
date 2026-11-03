# Before/after comparison on the home server: the same CMP plan against pt-lt-app:before and pt-lt-app:after, each on a
# fresh database seeded from perf/data/seedC. Start it detached (Claude Code reaps background shells under memory pressure):
#   Start-Process powershell -ArgumentList '-File perf/compare.ps1' -WindowStyle Hidden
# Images: built on the server from `git archive` of the commits (see perf/README.md).
# -Versions picks the image tags, -Plan the LoadLab phases (CMP = everything; CMP,C0,C3 = the cold mass start only).
param([string[]]$Versions = @('before', 'after'), [string]$Plan = 'CMP', [string]$Tag = 'cmp')
$ErrorActionPreference = 'Continue'   # PowerShell 5.1 turns native stderr into terminating errors under 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$server = 'tadzesi@192.168.1.2'
$log = "perf/data/$Tag.log"
function Log($m) { Add-Content -Path $log -Value "$(Get-Date -Format o) $m" -Encoding UTF8 }

$apiKey = (ssh $server "grep '^API_KEY=' ~/pt-loadtest/.env | cut -d= -f2-").Trim()
$dll = 'tools/LoadLab/bin/Release/net10.0/LoadLab.dll'

foreach ($version in $Versions) {
    $out = "perf/data/$Tag-$version"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Log "reset $version"
    ssh $server "cd ~/pt-loadtest && APP_IMAGE=pt-lt-app:$version ./reset.sh 2>&1" | ForEach-Object { Log "  $_" }
    Start-Sleep -Seconds 20   # counters and pgstat sidecars attach
    Log "run $version"
    & dotnet $dll run http://192.168.1.2:18080 perf/data/seedC $apiKey site/assets/example-trat.gpx $out 1 $Plan *>> "$out.console.log"
    Log "run $version exit $LASTEXITCODE"
    ssh $server "cd ~/pt-loadtest && sudo -n pkill -f '^python3 sampler.py'; pkill -f '^python3 jitter.py'; docker compose stop counters pgstat nginx 2>&1" | ForEach-Object { Log "  $_" }
    Start-Sleep -Seconds 3
    New-Item -ItemType Directory -Force "$out-server" | Out-Null
    scp -r -q "${server}:pt-loadtest/data/*" "$out-server/"
    Log "collected $version"
}

ssh $server "cd ~/pt-loadtest && docker compose down -v 2>&1" | ForEach-Object { Log "  $_" }
Log 'DONE'
