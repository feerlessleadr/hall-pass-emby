<#
Live check of the Hall Pass phone alerts on the local test Emby server (see CLAUDE.md for the server).
It backs up the plugin and its data, deploys the built DLL, watches the alerts through throw-away administrator and non-administrator
websockets, runs the real flows (scan, cancelled scan, apply, undo, cancelled apply, the automatic scan of new tracks, an interrupted job
found after a restart), then removes everything it made and restores the plugin, its settings and its data files from the backup.

  powershell -ExecutionPolicy Bypass -File tools\Test-HallPassNotifications.ps1 [-DllPath <path>] [-SkipAutoScan] [-Force]

Needs: the admin API key in %USERPROFILE%\.emby-spike-key (never printed), music in the test library. Never touches the server's real users:
it creates two throw-away users (one administrator, one not) and deletes them. Exit code 0 only when every check passed.
#>
param(
  [string]$DllPath = '',
  [string]$ServerRoot = (Join-Path $env:USERPROFILE 'Downloads\embyserver-win-x64-4.10.1.0'),
  [string]$MediaRoot = 'C:\claude\Music',
  [switch]$SkipAutoScan,
  [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[Environment]::CurrentDirectory = $repo
if ($DllPath -eq '') { $DllPath = Join-Path $repo 'src\HallPass\bin\Release\net6.0\HallPass.dll' }
if (-not (Test-Path $DllPath)) { throw "Build first: $DllPath not found." }

$base = 'http://localhost:8096/emby'
$pluginId = '6ca80343-2b60-40de-ab81-7a41628fcaf2'
$pluginsDir = Join-Path $ServerRoot 'programdata\plugins'
$configDir = Join-Path $pluginsDir 'configurations'
$journalDir = Join-Path $configDir 'HallPass.Journal'
$key = (Get-Content (Join-Path $env:USERPROFILE '.emby-spike-key')).Trim()
$H = @{ 'X-Emby-Token' = $key }
$results = New-Object System.Collections.ArrayList
$backup = Join-Path $env:TEMP ('hp-live-backup-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$autoDir = Join-Path $MediaRoot ('hp-live-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$wasRunning = $true   # set below, once Test-Server exists
$admin = $null; $plain = $null
$adminSock = $null; $plainSock = $null; $appSock = $null; $appPlain = $null; $appIdle = $null
$deployed = $false
function Test-Server { @(Get-Process -Name EmbyServer -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($ServerRoot, [StringComparison]::OrdinalIgnoreCase) }) }
$wasRunning = ((Test-Server).Count -gt 0)

function Check([string]$name, [bool]$ok, [string]$detail = '') {
  [void]$results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
  if ($ok) { Write-Host ("  ok   " + $name) } else { Write-Host ("  FAIL " + $name + "  " + $detail) -ForegroundColor Red }
}
function Api([string]$method, [string]$path, $body = $null, $headers = $H) {
  $a = @{ Method = $method; Uri = "$base$path"; Headers = $headers }
  if ($body -ne $null) { $a.Body = ($body | ConvertTo-Json -Depth 8); $a.ContentType = 'application/json' }
  Invoke-RestMethod @a
}
function WaitUntil([scriptblock]$cond, [int]$seconds, [int]$every = 2) {
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep $every }
  return $false
}
# only the shared test server's own process (another session may run a private Emby from a different folder)
function Test-Server { @(Get-Process -Name EmbyServer -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($ServerRoot, [StringComparison]::OrdinalIgnoreCase) }) }
function Stop-Emby {
  Test-Server | Stop-Process -Force
  $null = WaitUntil { (Test-Server).Count -eq 0 } 30 1
  Start-Sleep 3
}
function Start-Emby {
  # the tray program may start the server by itself; only start it if nothing came up
  Start-Sleep 6
  if ((Test-Server).Count -eq 0) {
    $sys = Join-Path $ServerRoot 'system'
    Start-Process (Join-Path $sys 'EmbyServer.exe') -ArgumentList ('-programdata "' + (Join-Path $ServerRoot 'programdata') + '" -ffmpeg "' + (Join-Path $sys 'ffmpeg.exe') + '" -ffprobe "' + (Join-Path $sys 'ffprobe.exe') + '" -restartexitcode 3') -WindowStyle Minimized
  }
  $up = WaitUntil { try { $null = Invoke-RestMethod -Headers $H "$base/Users"; $true } catch { $false } } 180 3
  if (-not $up) { throw 'Emby did not come up.' }
  Start-Sleep 8
}
function New-Sock($token, [string]$deviceId, [switch]$AppStyle) {
  $ws = New-Object System.Net.WebSockets.ClientWebSocket
  if ($AppStyle) {
    # the way the Embidextrous app connects: token ONLY in the X-Emby-Token header, deviceId in the address, no api_key
    $ws.Options.SetRequestHeader('X-Emby-Token', $token)
    $ws.ConnectAsync([Uri]("ws://localhost:8096/embywebsocket?deviceId=$deviceId"), [Threading.CancellationToken]::None).Wait()
  } else {
    $ws.ConnectAsync([Uri]("ws://localhost:8096/embywebsocket?api_key=$token&deviceId=$deviceId"), [Threading.CancellationToken]::None).Wait()
  }
  return @{ Ws = $ws; Buf = (New-Object byte[] 65536); Pending = $null; Stream = (New-Object IO.MemoryStream) }
}
# Reads a socket for a while and returns the HallPass.* messages it saw as objects.
function Send-Sock($s, [string]$text) {
  $bytes = [Text.Encoding]::UTF8.GetBytes($text)
  $s.Ws.SendAsync([ArraySegment[byte]]$bytes, [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
}
# the start message: delay, period, optional afterSeq, and the token (Emby never links a header-token socket to a user, so the token travels in the message)
function Sub-Start($s, [string]$token, [string]$after = '') { Send-Sock $s ('{"MessageType":"HallPassEventsStart","Data":"0,30000,' + $after + ',' + $token + '"}') }
# the events carried by the HallPassEvents messages in a list (flattened)
function Flat-Events($msgs) { @(foreach ($m in @($msgs)) { if ($m.MessageType -eq 'HallPassEvents') { foreach ($e in @($m.Data.Events)) { $e } } }) }
function Read-Sock($s, [int]$seconds, [string]$like = 'HallPass.*') {
  $out = New-Object System.Collections.ArrayList
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) {
    if ($s.Pending -eq $null) { $s.Pending = $s.Ws.ReceiveAsync([ArraySegment[byte]]$s.Buf, [Threading.CancellationToken]::None) }
    if ($s.Pending.Wait(500)) {
      $res = $s.Pending.Result; $s.Pending = $null
      $s.Stream.Write($s.Buf, 0, $res.Count)
      if ($res.EndOfMessage) {
        $text = [Text.Encoding]::UTF8.GetString($s.Stream.ToArray()); $s.Stream.SetLength(0)
        try { $m = $text | ConvertFrom-Json; if ($m.MessageType -like $like) { [void]$out.Add($m) } } catch {}
      }
    }
  }
  return $out
}
function Names($msgs) { (@($msgs) | ForEach-Object { $_.MessageType }) -join ',' }
function Scan-State { (Api GET '/HallPass/Status').Job }
function Write-State { (Api GET '/HallPass/Status').Write }
function Wait-Scan([int]$seconds = 300) { $null = WaitUntil { (Scan-State).State -ne 'Running' } $seconds 2; Scan-State }
function Wait-Write([int]$seconds = 300) { $null = WaitUntil { (Write-State).State -ne 'Running' } $seconds 1; Write-State }
function Set-Config([hashtable]$changes) {
  $cfg = Api GET "/Plugins/$pluginId/Configuration"
  foreach ($k in $changes.Keys) { $cfg | Add-Member -NotePropertyName $k -NotePropertyValue $changes[$k] -Force }
  Api POST "/Plugins/$pluginId/Configuration" $cfg | Out-Null
}
function Tag-Snapshot {
  $items = (Api GET '/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Tags,LockedFields&Limit=5000').Items
  (@($items | Sort-Object Id | ForEach-Object { "$($_.Id):" + ((@($_.Tags) | Sort-Object) -join ',') + ':' + ((@($_.LockedFields) | Sort-Object) -join ',') }) -join "`n")
}
function Audio-Count { [int](Api GET '/Items?Recursive=true&IncludeItemTypes=Audio&Limit=1').TotalRecordCount }
function Hash([string]$p) { (Get-FileHash $p -Algorithm SHA256).Hash }
function Events-Since([long]$after) { Api GET "/HallPass/Events?After=$after&Limit=500" }
function Connect-Socks {
  $script:adminSock = New-Sock $script:adminToken 'hp-live-admin'
  $script:plainSock = New-Sock $script:plainToken 'hp-live-plain'
  $script:appSock = New-Sock $script:adminToken 'hp-live-app' -AppStyle
  $script:appPlain = New-Sock $script:plainToken 'hp-live-app2' -AppStyle
  $script:appIdle = New-Sock $script:adminToken 'hp-live-app3' -AppStyle
}

try {
  Write-Host "== Hall Pass phone alerts live test ($DllPath)"
  if ($wasRunning) {
    $all = Invoke-RestMethod -Headers $H "$base/Sessions"
    $busy = @(foreach ($s in $all) { if ($s.UserName -and $s.UserName -notlike 'hp-live-*' -and $s.LastActivityDate -and ([datetime]$s.LastActivityDate).ToUniversalTime() -gt (Get-Date).AddMinutes(-10).ToUniversalTime()) { $s } })
    if ($busy.Count -gt 0 -and -not $Force) { throw 'Someone used the test server in the last 10 minutes. Run again later, or use -Force.' }
  }

  Write-Host '-- back up the plugin and its data'
  New-Item -ItemType Directory -Force $backup | Out-Null
  $files = @('HallPass.xml', 'HallPass.Evidence.json', 'HallPass.OnlineCache.json')
  Copy-Item (Join-Path $pluginsDir 'HallPass.dll') (Join-Path $backup 'HallPass.dll')
  foreach ($f in $files) { if (Test-Path (Join-Path $configDir $f)) { Copy-Item (Join-Path $configDir $f) (Join-Path $backup $f) } }
  if (Test-Path $journalDir) { Copy-Item $journalDir (Join-Path $backup 'HallPass.Journal') -Recurse }
  $origHash = @{}
  Get-ChildItem $backup -File | ForEach-Object { $origHash[$_.Name] = Hash $_.FullName }
  $origJournal = @(Get-ChildItem $journalDir -File | ForEach-Object { $_.Name + ':' + $_.Length }) -join ';'
  Check 'The backup was made' ($origHash.ContainsKey('HallPass.dll'))

  Write-Host '-- deploy (with a few tracks taken out of the saved results, so the first scan finds them as new)'
  Stop-Emby
  $deployed = $true
  Copy-Item $DllPath (Join-Path $pluginsDir 'HallPass.dll') -Force
  # The saved results are one JSON line; each track starts with {"ItemId": and nothing nested starts that way, so split there.
  $text = [IO.File]::ReadAllText((Join-Path $configDir 'HallPass.Evidence.json'))
  $parts = [regex]::Split($text, '(?=\{"ItemId":)')
  $dropped = 0
  $sb = New-Object Text.StringBuilder
  for ($i = 0; $i -lt $parts.Count; $i++) {
    $isItem = $i -gt 0 -and $i -lt ($parts.Count - 1)   # never the last one: it carries the closing brackets
    if ($isItem -and $dropped -lt 5 -and $parts[$i].Contains('"Decision":"Review"')) { $dropped++; continue }
    [void]$sb.Append($parts[$i])
  }
  [IO.File]::WriteAllText((Join-Path $configDir 'HallPass.Evidence.json'), $sb.ToString())
  Check 'Five review tracks were taken out of the saved results' ($dropped -eq 5)
  Start-Emby

  Write-Host '-- routes'
  $info = Api GET '/HallPass/Info'
  Check 'Info has the new version, ApiVersion 1 and ApiLevel 1' ($info.Version -eq '1.4.0.0' -and $info.ApiVersion -eq 1 -and $info.ApiLevel -eq 1) ($info | ConvertTo-Json -Compress)
  Check 'Info lists the events and catalog features' ((@($info.Features) -contains 'events') -and (@($info.Features) -contains 'catalog'))
  $cat = Api GET '/HallPass/Notifications/Catalog'
  Check 'The catalog has the five alerts and the service block' (@($cat.Events).Count -eq 5 -and $cat.Service.Id -eq 'HallPass' -and $cat.Service.ChannelId -eq 'hall-pass') ((@($cat.Events) | ForEach-Object { $_.Id }) -join ',')
  $rq = @($cat.Events | Where-Object { $_.Id -eq 'HallPass.ReviewQueue' })[0]
  Check 'ReviewQueue is off by default and has MinCount' ($rq.DefaultRule.Enabled -eq $false -and $rq.Params[0].Name -eq 'MinCount' -and $rq.Params[0].Default -eq 50)
  $ev0 = Api GET '/HallPass/Events'
  Check 'The catch-up route answers in the documented shape' ($ev0.LatestSeq -ne $null -and $ev0.Truncated -eq $false -and ($ev0.PSObject.Properties.Name -contains 'Events'))
  $startSeq = [long]$ev0.LatestSeq
  $page = Invoke-WebRequest -UseBasicParsing -Headers $H "$base/web/configurationpage?name=HallPassConfiguration"
  Check 'The settings page has the Phone alerts section' ($page.Content -match 'hpSecAlerts' -and $page.Content -match 'Send phone alerts')
  $anon = $false; try { Invoke-RestMethod "$base/HallPass/Events" | Out-Null } catch { $anon = $true }
  Check 'A request with no token is refused' $anon

  Write-Host '-- throw-away users and websockets'
  $pw = [Guid]::NewGuid().ToString('N') + 'Aa1!'
  $admin = Api POST '/Users/New' @{ Name = 'hp-live-admin' }
  Api POST "/Users/$($admin.Id)/Password" @{ NewPw = $pw; CurrentPw = '' } | Out-Null
  $p = (Api GET "/Users/$($admin.Id)").Policy; $p.IsAdministrator = $true
  Api POST "/Users/$($admin.Id)/Policy" $p | Out-Null
  $plain = Api POST '/Users/New' @{ Name = 'hp-live-plain' }
  Api POST "/Users/$($plain.Id)/Password" @{ NewPw = $pw; CurrentPw = '' } | Out-Null
  $hdr = @{ 'X-Emby-Authorization' = 'MediaBrowser Client="hp-live", Device="probe", DeviceId="hp-live-1", Version="1"' }
  $script:adminToken = (Api POST '/Users/AuthenticateByName' @{ Username = 'hp-live-admin'; Pw = $pw } $hdr).AccessToken
  $script:plainToken = (Api POST '/Users/AuthenticateByName' @{ Username = 'hp-live-plain'; Pw = $pw } $hdr).AccessToken
  Connect-Socks
  Check 'Both websockets connected' ($adminSock.Ws.State -eq 'Open' -and $plainSock.Ws.State -eq 'Open')
  Check 'The app-style sockets (token only in the header, no api_key) connected' ($appSock.Ws.State -eq 'Open' -and $appPlain.Ws.State -eq 'Open' -and $appIdle.Ws.State -eq 'Open')
  Set-Config @{ SendPhoneAlerts = $true; AlertScanFinished = $true; AlertScanFailed = $true; AlertApplyFinished = $true; AlertApplyFailed = $true; AlertReviewQueue = $true; DryRun = $true; RunMetadataSavers = $false; JournalKeepJobs = 500; BatchSize = 200; BatchDelayMs = 50 }
  $null = Read-Sock $adminSock 1; $null = Read-Sock $plainSock 1

  Write-Host '-- subscribing the way the app does' 
  $seq0 = [long](Api GET '/HallPass/Events').LatestSeq
  Sub-Start $appSock $adminToken; Sub-Start $appPlain $plainToken
  $first = Read-Sock $appSock 4 'HallPassEvents'
  Check 'A subscribed administrator header-socket gets the baseline at once' (@($first).Count -ge 1 -and $first[0].Data.LatestSeq -eq $seq0 -and @($first[0].Data.Events).Count -eq 0 -and $first[0].Data.ApiLevel -eq 1 -and $first[0].Data.Truncated -eq $false) ($first | ConvertTo-Json -Compress -Depth 5)
  $errs = Read-Sock $appPlain 4 'HallPassEvents'
  Check 'A non-administrator who subscribes gets the Error reply and nothing more' (@($errs).Count -eq 1 -and $errs[0].Data.Error -eq 'Administrator login required.') ($errs | ConvertTo-Json -Compress -Depth 5)

  Write-Host '-- a scan the owner starts: summary, then the review queue went up'
  $null = Api POST '/HallPass/Scan' @{ Force = $false }
  $job = Wait-Scan
  $msgs = Read-Sock $adminSock 5
  $fin = @($msgs | Where-Object { $_.MessageType -eq 'HallPass.ScanFinished' })
  Check 'ScanFinished arrived on the administrator socket' ($job.State -eq 'Completed' -and $fin.Count -eq 1) ((Names $msgs) + ' / ' + $job.State)
  if ($fin.Count -eq 1) {
    $d = $fin[0].Data
    Check 'ScanFinished carries the same counts as the scan' ($d.Total -eq $job.Total -and $d.Scanned -eq $job.Scanned -and $d.Unchanged -eq $job.Unchanged -and $d.Failed -eq $job.Failed -and $d.OnlineFailed -eq $job.OnlineFailed -and $d.Scanned -ge 5) ($d | ConvertTo-Json -Compress)
    Check 'ScanFinished NeedsReview is the queue size now' ($d.NeedsReview -eq [int](Api GET '/HallPass/Status').Stats.ByDecision.Review) ([string]$d.NeedsReview)
  }
  $rqm = @($msgs | Where-Object { $_.MessageType -eq 'HallPass.ReviewQueue' })
  Check 'ReviewQueue arrived with the new count' ($rqm.Count -eq 1 -and $rqm[0].Data.New -ge 5 -and $rqm[0].Data.Count -eq [int](Api GET '/HallPass/Status').Stats.ByDecision.Review) (Names $msgs)
  Check 'Every alert carries the common fields' (@($msgs | Where-Object { $_.Data.ApiLevel -eq 1 -and $_.Data.Seq -gt $startSeq -and $_.Data.EventId -and $_.Data.ServerTimeUtc -match 'Z$' -and $_.Data.Event }).Count -eq @($msgs).Count -and @($msgs).Count -ge 2)
  $plainMsgs = Read-Sock $plainSock 1
  Check 'The non-administrator socket received nothing' (@($plainMsgs).Count -eq 0) (Names $plainMsgs)
  $ev = Events-Since $startSeq
  Check 'The catch-up route lists the same alerts in order' (@($ev.Events).Count -eq @($msgs).Count -and $ev.LatestSeq -eq $ev.Events[-1].Seq -and $ev.Events[0].Seq -lt $ev.Events[-1].Seq) ($ev | ConvertTo-Json -Compress -Depth 4)
  Check 'Asking after the latest number returns nothing' (@((Events-Since $ev.LatestSeq).Events).Count -eq 0)
  $raw = (@($msgs) | ConvertTo-Json -Depth 8)
  Check 'No alert carries a path' ($raw -notmatch '[A-Za-z]:\\' -and $raw -notmatch '\\\\' -and $raw -notmatch [regex]::Escape($MediaRoot))
  $lastSeq = [long]$ev.LatestSeq
  $appMsgs = Read-Sock $appSock 3 'HallPassEvents'
  $appEv = Flat-Events $appMsgs
  Check 'The subscribed header-socket received the same events (ScanFinished and ReviewQueue) with their numbers' ((@($appEv | ForEach-Object { $_.Name }) -join ',') -eq (@($ev.Events | ForEach-Object { $_.Name }) -join ',') -and @($appEv).Count -ge 2 -and $appEv[0].Seq -eq $ev.Events[0].Seq -and $appEv[0].Data.Event -eq 'ScanFinished') ((@($appEv | ForEach-Object { $_.Name }) -join ',') + ' vs ' + (@($ev.Events | ForEach-Object { $_.Name }) -join ','))
  Check 'No event was delivered twice on the subscribed socket' (@($appEv | Select-Object -ExpandProperty Seq -Unique).Count -eq @($appEv).Count)
  Check 'The unsubscribed header-socket received nothing' (@(Read-Sock $appIdle 1 'HallPass*').Count -eq 0)
  Check 'The non-administrator header-socket received nothing more' (@(Read-Sock $appPlain 1 'HallPass*').Count -eq 0)
  $scanSeq = [long]$appEv[0].Seq

  Write-Host '-- a second scan with nothing new: the owner still gets a summary, the queue does not repeat'
  $null = Api POST '/HallPass/Scan' @{ Force = $false }
  $job = Wait-Scan
  $m2 = Read-Sock $adminSock 4
  Check 'A scan I start reports even when nothing changed, and the queue is not announced again' ((Names $m2) -eq 'HallPass.ScanFinished' -and $m2[0].Data.Scanned -eq 0 -and $m2[0].Data.Seq -gt $lastSeq) (Names $m2)
  $lastSeq = [long](Api GET '/HallPass/Events').LatestSeq
  $null = Read-Sock $appSock 1 'HallPassEvents'

  Write-Host '-- heartbeat and resubscribing'
  $beat = Read-Sock $appSock 40 'HallPassEvents'
  Check 'A heartbeat (empty Events, LatestSeq) arrived within 30 seconds' (@($beat | Where-Object { @($_.Data.Events).Count -eq 0 -and $_.Data.LatestSeq -ge $lastSeq }).Count -ge 1) ([string]@($beat).Count)
  Sub-Start $appSock $adminToken "$scanSeq"
  $re = Read-Sock $appSock 4 'HallPassEvents'
  $reEv = Flat-Events $re
  Check 'Subscribing again with afterSeq replays only newer events' (@($reEv).Count -ge 1 -and @($reEv | Where-Object { $_.Seq -le $scanSeq }).Count -eq 0 -and $reEv[0].Seq -eq ($scanSeq + 1)) ((@($reEv | ForEach-Object { $_.Seq }) -join ',') + ' after ' + $scanSeq)
  Send-Sock $appSock '{"MessageType":"HallPassEventsStop"}'
  Start-Sleep 1
  $null = Read-Sock $appSock 1 'HallPassEvents'
  $stopSeq = [long](Api GET '/HallPass/Events').LatestSeq
  Write-Host '-- a cancelled scan'
  $null = Api POST '/HallPass/Scan' @{ Force = $true }
  Start-Sleep 2
  $null = Api POST '/HallPass/Scan/Cancel'
  $job = Wait-Scan
  $m3 = Read-Sock $adminSock 4
  $sf = @($m3 | Where-Object { $_.MessageType -eq 'HallPass.ScanFailed' })
  if ($job.State -eq 'Completed') {
    Check 'The forced scan was too quick to cancel (not a failure of the alerts; ScanFinished sent instead)' (@($m3 | Where-Object { $_.MessageType -eq 'HallPass.ScanFinished' }).Count -eq 1) (Names $m3)
  } else {
    $appFail = Read-Sock $appSock 1 'HallPassEvents'
  Check 'After HallPassEventsStop the app socket gets nothing more' (@($appFail).Count -eq 0) (@($appFail).Count.ToString())
  Check 'ScanFailed with State Cancelled and a plain sentence' ($sf.Count -eq 1 -and $sf[0].Data.State -eq 'Cancelled' -and $sf[0].Data.Message -match 'cancelled') (Names $m3)
  }
  $null = Api POST '/HallPass/Scan' @{ Force = $true }; $null = Wait-Scan
  $null = Read-Sock $adminSock 3
  $lastSeq = [long](Api GET '/HallPass/Events').LatestSeq

  Write-Host '-- apply and undo (real tag writes on the test library, put back afterwards)'
  $tagsBefore = Tag-Snapshot
  Set-Config @{ DryRun = $false }
  $null = Read-Sock $adminSock 1
  $null = Api POST '/HallPass/Apply' @{ Confirm = $true; ConfirmLarge = $true }
  $w = Wait-Write
  $m4 = Read-Sock $adminSock 4
  $af = @($m4 | Where-Object { $_.MessageType -eq 'HallPass.ApplyFinished' })
  Check 'ApplyFinished (Apply) carries the counts of the job' ($w.State -eq 'Completed' -and $af.Count -eq 1 -and $af[0].Data.Kind -eq 'Apply' -and $af[0].Data.Changed -eq $w.Changed -and $af[0].Data.Changed -gt 0 -and $af[0].Data.Failed -eq $w.Failed) ((Names $m4) + ' ' + ($w | ConvertTo-Json -Compress))
  $applyJob = $w.JobId
  $null = Api POST "/HallPass/Jobs/$applyJob/Undo" @{ Force = $false }
  $w = Wait-Write
  $m5 = Read-Sock $adminSock 4
  $au = @($m5 | Where-Object { $_.MessageType -eq 'HallPass.ApplyFinished' })
  Check 'ApplyFinished (Undo) arrived' ($au.Count -eq 1 -and $au[0].Data.Kind -eq 'Undo' -and $au[0].Data.Changed -gt 0) (Names $m5)
  Check 'The undo put every tag and lock back' ((Tag-Snapshot) -eq $tagsBefore)

  Write-Host '-- a cancelled apply'
  Set-Config @{ BatchSize = 1; BatchDelayMs = 400 }
  $null = Read-Sock $adminSock 1
  $null = Api POST '/HallPass/Apply' @{ Confirm = $true; ConfirmLarge = $true }
  Start-Sleep 4
  $null = Api POST '/HallPass/Write/Cancel'
  $w = Wait-Write
  $cancelJob = $w.JobId
  $m6 = Read-Sock $adminSock 4
  $afail = @($m6 | Where-Object { $_.MessageType -eq 'HallPass.ApplyFailed' })
  Check 'ApplyFailed with State Cancelled and Kind Apply' ($w.State -eq 'Cancelled' -and $afail.Count -eq 1 -and $afail[0].Data.State -eq 'Cancelled' -and $afail[0].Data.Kind -eq 'Apply' -and $afail[0].Data.Message) ((Names $m6) + ' ' + $w.State)
  Check 'A cancelled apply is not also reported as finished' (@($m6 | Where-Object { $_.MessageType -eq 'HallPass.ApplyFinished' }).Count -eq 0)
  Set-Config @{ BatchSize = 200; BatchDelayMs = 50 }
  $null = Api POST "/HallPass/Jobs/$cancelJob/Undo" @{ Force = $false }
  $null = Wait-Write
  $null = Read-Sock $adminSock 3
  Check 'Undoing the cancelled apply put every tag and lock back' ((Tag-Snapshot) -eq $tagsBefore)
  Set-Config @{ DryRun = $true }

  Write-Host '-- switches'
  $lastSeq = [long](Api GET '/HallPass/Events').LatestSeq
  Set-Config @{ SendPhoneAlerts = $false }
  $null = Api POST '/HallPass/Scan' @{ Force = $false }; $null = Wait-Scan
  $m7 = Read-Sock $adminSock 3
  Check 'With the master switch off nothing is sent or logged' (@($m7).Count -eq 0 -and [long](Api GET '/HallPass/Events').LatestSeq -eq $lastSeq) (Names $m7)
  Set-Config @{ SendPhoneAlerts = $true; AlertScanFinished = $false }
  $null = Api POST '/HallPass/Scan' @{ Force = $false }; $null = Wait-Scan
  $m8 = Read-Sock $adminSock 3
  Check 'With only ScanFinished off that alert is neither sent nor logged' (@($m8).Count -eq 0 -and [long](Api GET '/HallPass/Events').LatestSeq -eq $lastSeq) (Names $m8)
  Set-Config @{ AlertScanFinished = $true }

  if (-not $SkipAutoScan) {
    Write-Host '-- the automatic scan of new tracks'
    $source = @(Get-ChildItem $MediaRoot -Recurse -File -Filter *.mp3 | Sort-Object Length | Select-Object -First 1)[0].FullName
    $audioBefore = Audio-Count
    $lastSeq = [long](Api GET '/HallPass/Events').LatestSeq
    New-Item -ItemType Directory -Force $autoDir | Out-Null
    1..3 | ForEach-Object { Copy-Item $source (Join-Path $autoDir ('hp-live-small-{0:00}.mp3' -f $_)) }
    $null = Api POST '/Library/Refresh'
    $seen = WaitUntil { (Audio-Count) -ge ($audioBefore + 3) } 240 5
    Check 'Emby found the 3 new tracks' $seen ([string](Audio-Count))
    $auto0 = (Api GET '/HallPass/Status').Auto.Processed
    $scanned = WaitUntil { (Api GET '/HallPass/Status').Auto.Processed -ge ($auto0 + 3) } 180 5
    $m9 = Read-Sock $adminSock 15
    Check 'The automatic scan looked at them' $scanned ([string](Api GET '/HallPass/Status').Auto.Processed)
    Check 'A quiet automatic scan of 3 tracks sent nothing' (@($m9 | Where-Object { $_.MessageType -eq 'HallPass.ScanFinished' }).Count -eq 0) (Names $m9)

    $auto1 = (Api GET '/HallPass/Status').Auto.Processed
    4..33 | ForEach-Object { Copy-Item $source (Join-Path $autoDir ('hp-live-big-{0:00}.mp3' -f $_)) }
    $null = Api POST '/Library/Refresh'
    $seen = WaitUntil { (Audio-Count) -ge ($audioBefore + 33) } 240 5
    Check 'Emby found the 30 more tracks' $seen ([string](Audio-Count))
    $m10 = Read-Sock $adminSock 120
    $big = @($m10 | Where-Object { $_.MessageType -eq 'HallPass.ScanFinished' })
    $sum = 0; foreach ($b in $big) { $sum += [int]$b.Data.Scanned }
    Check 'A big automatic scan sent a ScanFinished summary (one summary for the 30 tracks)' ($big.Count -ge 1 -and $sum -ge 25) ((Names $m10) + ' scanned=' + $sum)
    Check 'The summary is not one per track' ($big.Count -le 2) ([string]$big.Count)

    Write-Host '   (removing the copied tracks)'
    [IO.Directory]::Delete($autoDir, $true)
    $null = Api POST '/Library/Refresh'
    $gone = WaitUntil { (Audio-Count) -le $audioBefore } 240 5
    Check 'Emby dropped the copied tracks again' $gone ([string](Audio-Count))
  }

  Write-Host '-- a job found unfinished after a restart'
  $lastSeq = [long](Api GET '/HallPass/Events').LatestSeq
  $startedOwn = $true
  Stop-Emby
  $fakeId = [Guid]::NewGuid().ToString('N')
  $header = '{"Type":"job","JobId":"' + $fakeId + '","Kind":"apply","StartedUtc":"' + (Get-Date).ToUniversalTime().AddHours(-1).ToString('yyyy-MM-ddTHH:mm:ss.0000000Z') + '","UndoOf":null}'
  [IO.File]::WriteAllText((Join-Path $journalDir ($fakeId + '.jsonl')), $header + "`n")
  Start-Emby
  $ev = Events-Since $lastSeq
  $intr = @($ev.Events | Where-Object { $_.Name -eq 'HallPass.ApplyFailed' -and $_.Data.State -eq 'Interrupted' })
  Check 'The unfinished job was announced once, after the restart, with a plain sentence' ($intr.Count -eq 1 -and $intr[0].Seq -eq ($lastSeq + 1) -and $intr[0].Data.Message -match 'did not finish') ($ev | ConvertTo-Json -Compress -Depth 4)
  Check 'Sequence numbers continued across the restart' ($intr.Count -ge 1 -and $intr[0].Seq -gt $lastSeq)
  Check 'The interrupted-job alert carries no path' (($intr | ConvertTo-Json -Depth 6) -notmatch '[A-Za-z]:\\')
  $afterFirst = [long](Api GET '/HallPass/Events').LatestSeq
  Stop-Emby
  Start-Emby
  Check 'A second restart did not announce it again' ([long](Api GET '/HallPass/Events').LatestSeq -eq $afterFirst) ([string](Api GET '/HallPass/Events').LatestSeq)
  $trunc = Api GET '/HallPass/Events?After=0&Limit=3'
  Check 'Limit is honored' (@($trunc.Events).Count -le 3)
}
catch {
  Check 'The test run finished without an unexpected error' $false ($_.Exception.Message + ' @ line ' + $_.InvocationInfo.ScriptLineNumber)
}
finally {
  Write-Host '-- cleanup'
  foreach ($s in @($adminSock, $plainSock, $appSock, $appPlain, $appIdle)) { try { if ($s) { $s.Ws.Dispose() } } catch {} }
  foreach ($u in @($admin, $plain)) { try { if ($u) { Invoke-RestMethod -Method Delete -Headers $H "$base/Users/$($u.Id)" | Out-Null; Write-Host "  throw-away user deleted" } } catch { Write-Host ('  could not delete a throw-away user: ' + $_.Exception.Message) -ForegroundColor Yellow } }
  try { if (Test-Path $autoDir) { [IO.Directory]::Delete($autoDir, $true); try { Invoke-RestMethod -Method Post -Headers $H "$base/Library/Refresh" | Out-Null } catch {} } } catch {}
  if ($deployed) {
    Stop-Emby
    Copy-Item (Join-Path $backup 'HallPass.dll') (Join-Path $pluginsDir 'HallPass.dll') -Force
    foreach ($f in $files) { if (Test-Path (Join-Path $backup $f)) { Copy-Item (Join-Path $backup $f) (Join-Path $configDir $f) -Force } }
    if (Test-Path $journalDir) { [IO.Directory]::Delete($journalDir, $true) }
    if (Test-Path (Join-Path $backup 'HallPass.Journal')) { Copy-Item (Join-Path $backup 'HallPass.Journal') $journalDir -Recurse }
    foreach ($f in @('HallPass.Events.json', 'HallPass.Events.json.seq', 'HallPass.Events.json.corrupt', 'HallPass.Events.json.tmp', 'HallPass.Notifications.json', 'HallPass.Notifications.json.tmp')) {
      $p = Join-Path $configDir $f; if (Test-Path $p) { [IO.File]::Delete($p) }
    }
    $same = $true
    foreach ($k in $origHash.Keys) {
      $target = if ($k -eq 'HallPass.dll') { Join-Path $pluginsDir $k } else { Join-Path $configDir $k }
      if ((Hash $target) -ne $origHash[$k]) { $same = $false; Write-Host "  NOT restored: $k" -ForegroundColor Red }
    }
    $journalNow = @(Get-ChildItem $journalDir -File | ForEach-Object { $_.Name + ':' + $_.Length }) -join ';'
    if ($journalNow -ne $origJournal) { $same = $false; Write-Host '  journal differs from the backup' -ForegroundColor Red }
    Check 'The plugin DLL, its settings, results, online cache and journal are back exactly as found (by hash)' $same
    if ($wasRunning) { Start-Emby; Write-Host '  server restarted (it was running before)' } else { Write-Host '  server left stopped (it was stopped before)' }
  }
  if (Test-Path $backup) { [IO.Directory]::Delete($backup, $true) }
  $failed = @($results | Where-Object { -not $_.Ok })
  Write-Host ''
  Write-Host ("== {0} checks, {1} failed" -f $results.Count, $failed.Count)
  if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
}
