# The public URL for this machine's Smarty, and a way to ask what it is.
#
# A cloudflared quick tunnel prints its hostname exactly once, when it starts, and then never again — so a tunnel
# that has been up for a day is indistinguishable from one that died, and the only way to be sure was to restart it
# and get a different URL. It also outlives the API it points at, because it proxies a port rather than a process:
# restarting the API does not need a new tunnel, and killing the tunnel to find out its name is the one thing
# guaranteed to break the link already saved on a phone.
#
# So: if one is already serving this port, say what it is. Only start one if there isn't.
#
# Usage:  .\scripts\tunnel.ps1 [-Port 5179] [-Restart]

param(
    [int]$Port = 5179,
    # Force a fresh tunnel. Changes the public URL — every saved link stops working.
    [switch]$Restart
)

$ErrorActionPreference = 'Stop'

# cloudflared's metrics server is where a running tunnel will tell you its own name. Left unspecified it picks a
# random port, so anything we start gets a fixed one; anything already running gets found by asking Windows which
# port it listens on.
$MetricsPort = 20241

function Get-TunnelUrl([int]$metrics) {
    try {
        $res = Invoke-RestMethod -Uri "http://127.0.0.1:$metrics/quicktunnel" -TimeoutSec 3
        if ($res.hostname) { return "https://" + $res.hostname }
    } catch { }
    return $null
}

function Find-Existing([int]$port) {
    foreach ($p in @(Get-Process cloudflared -ErrorAction SilentlyContinue)) {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)" -ErrorAction SilentlyContinue).CommandLine
        if (-not $cmd -or $cmd -notmatch [regex]::Escape("localhost:$port")) { continue }

        # Its metrics port, whatever it happened to pick. A tunnel started by hand won't be on ours.
        $listening = Get-NetTCPConnection -OwningProcess $p.Id -State Listen -ErrorAction SilentlyContinue
        foreach ($l in @($listening)) {
            $url = Get-TunnelUrl $l.LocalPort
            if ($url) { return [pscustomobject]@{ Pid = $p.Id; Url = $url } }
        }
        return [pscustomobject]@{ Pid = $p.Id; Url = $null }
    }
    return $null
}

$existing = Find-Existing $Port

if ($existing -and $Restart) {
    Write-Host "Stopping the tunnel on pid $($existing.Pid) — the public URL is about to change." -ForegroundColor Yellow
    Stop-Process -Id $existing.Pid -Force
    Start-Sleep -Seconds 1
    $existing = $null
}

if ($existing) {
    if ($existing.Url) {
        Write-Host "Tunnel already up (pid $($existing.Pid)): $($existing.Url)" -ForegroundColor Green
    } else {
        # Running and serving the right port, but not saying its name — an older one started without a reachable
        # metrics server. Still working, so it is left alone: a new one would change the URL.
        Write-Host "Tunnel already up (pid $($existing.Pid)), but it won't say its URL." -ForegroundColor Yellow
        Write-Host "Run with -Restart to replace it (this changes the public URL)." -ForegroundColor DarkGray
    }
    return
}

if (-not (Get-Command cloudflared -ErrorAction SilentlyContinue)) {
    Write-Host "cloudflared isn't on PATH — no public URL. The app still works on http://localhost:$Port." -ForegroundColor Yellow
    return
}

Write-Host "Starting a tunnel to http://localhost:$Port …" -ForegroundColor DarkGray
Start-Process -WindowStyle Hidden cloudflared -ArgumentList @(
    'tunnel', '--url', "http://localhost:$Port", '--no-autoupdate', '--metrics', "127.0.0.1:$MetricsPort"
)

# The hostname is assigned after the tunnel connects, so it isn't there the instant the process starts.
$url = $null
foreach ($attempt in 1..20) {
    Start-Sleep -Milliseconds 750
    $url = Get-TunnelUrl $MetricsPort
    if ($url) { break }
}

if ($url) {
    Write-Host "Tunnel up: $url" -ForegroundColor Green
} else {
    Write-Host "The tunnel didn't report a URL. Check it with: cloudflared tunnel --url http://localhost:$Port" -ForegroundColor Yellow
}
