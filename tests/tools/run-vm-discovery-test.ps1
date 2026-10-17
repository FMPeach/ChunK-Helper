param([switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
# Diagnostic only. PowerShell 5.1 compatible; no install, elevation, firewall or service changes.
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixture.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$xmlPath = Join-Path $PSScriptRoot 'description.xml'
$xml = [IO.File]::ReadAllBytes($xmlPath)
$hash = (Get-FileHash -LiteralPath $xmlPath -Algorithm SHA256).Hash
if ($hash -ne $config.DescriptionSha256) { throw 'Fixture description hash mismatch.' }
$types = @('upnp:rootdevice', [string]$config.Udn, 'urn:schemas-upnp-org:device:MediaRenderer:1',
    'urn:schemas-upnp-org:service:AVTransport:1', 'urn:schemas-upnp-org:service:ConnectionManager:1',
    'urn:schemas-upnp-org:service:RenderingControl:1', 'urn:schemas-upnp-org:service:GeneralControl:1')
if ($ValidateOnly) { Write-Host "Fixture OK: $($config.Udn), XML SHA256 $hash"; exit 0 }

$hostIp = [Net.IPAddress]::Parse($config.HostAddress)
$guestIp = [Net.IPAddress]::Parse($config.GuestAddress)
$owned = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | ForEach-Object {
    $_.GetIPProperties().UnicastAddresses | ForEach-Object { $_.Address.ToString() }
})
if ($owned -notcontains $guestIp.ToString()) { throw "This VM does not own $guestIp. Tell the host operator the current VM IPv4 address; regenerate the bundle." }
$logPath = Join-Path $PSScriptRoot 'vm-result.log'
$log = New-Object 'Collections.Generic.List[string]'
function Log([string]$text) {
    $line = ('[{0}] {1}' -f [DateTimeOffset]::Now.ToString('HH:mm:ss.fff'), $text)
    $log.Add($line)
    Write-Host $line
}
function Packet([string]$kind, [string]$type, [string]$location) {
    $usn = if ($type -eq $config.Udn) { [string]$config.Udn } else { "$($config.Udn)::$type" }
    $header = "NOTIFY * HTTP/1.1`r`nHOST: 239.255.255.250:1900`r`n"
    if ($kind -eq 'alive') { $header += "CACHE-CONTROL: max-age=60`r`nLOCATION: $location`r`n" }
    $header += "NT: $type`r`nNTS: ssdp:$kind`r`n"
    if ($kind -eq 'alive') { $header += "SERVER: Windows/10.0 UPnP/1.0 ChunKHelper/0.1`r`n" }
    return [Text.Encoding]::ASCII.GetBytes($header + "USN: $usn`r`n`r`n")
}
$sender = $null
$listener = $null
$active = $false
try {
    $sender = New-Object Net.Sockets.UdpClient
    $sender.Client.Bind((New-Object Net.IPEndPoint($guestIp, 0)))
    $sender.Client.SetSocketOption([Net.Sockets.SocketOptionLevel]::IP, [Net.Sockets.SocketOptionName]::MulticastInterface, $guestIp.GetAddressBytes())
    $sender.Client.SetSocketOption([Net.Sockets.SocketOptionLevel]::IP, [Net.Sockets.SocketOptionName]::MulticastTimeToLive, 2)
    $sender.MulticastLoopback = $true
    $ssdp = New-Object Net.IPEndPoint([Net.IPAddress]::Parse('239.255.255.250'), 1900)
    $mirror = New-Object Net.IPEndPoint([Net.IPAddress]::Parse('239.255.255.250'), [int]$config.DiagnosticPort)
    $marker = New-Object Net.IPEndPoint($hostIp, [int]$config.DiagnosticPort)
    function Mark([string]$phase, [string]$state) {
        $bytes = [Text.Encoding]::ASCII.GetBytes("CHUNK_TEST|$($config.RunId)|$phase|$state")
        [void]$sender.Send($bytes, $bytes.Length, $marker)
        # Separate HTTP progress marker: UDP delivery itself is under investigation.
        $request = [Net.HttpWebRequest]::Create("http://$hostIp`:$($config.HttpPort)/diagnostic-phase/$($config.RunId)/$phase/$state")
        $request.Proxy = $null
        $request.Timeout = 2000
        $request.UserAgent = 'ChunKABGuestMarker/1.0'
        try { $response = $request.GetResponse(); $response.Dispose() }
        catch { Log "HTTP progress marker failed: $($_.Exception.Message)" }
        Log "$phase $state"
    }
    function Advertise([string]$kind, [string]$location) {
        foreach ($type in $types) {
            $bytes = Packet $kind $type $location
            [void]$sender.Send($bytes, $bytes.Length, $ssdp)
            [void]$sender.Send($bytes, $bytes.Length, $mirror)
        }
    }
    $listener = New-Object Net.Sockets.TcpListener($guestIp, [int]$config.HttpPort)
    $listener.Start()
    $active = $true
    Log "Sender=$($sender.Client.LocalEndPoint); HTTP=$guestIp`:$($config.HttpPort); UDN=$($config.Udn)"
    Log "XML SHA256=$hash"
    $web = New-Object Net.WebClient
    try {
        $web.Proxy = $null
        $web.Headers['User-Agent'] = 'ChunKABGuestPreflight/1.0'
        $hostXml = $web.DownloadData("http://$hostIp`:$($config.HttpPort)/device/description.xml")
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hostHash = [BitConverter]::ToString($sha.ComputeHash($hostXml)).Replace('-', '') } finally { $sha.Dispose() }
        if ($hostHash -ne $hash) { throw 'Host and VM XML differ. Check that the observer uses this bundle.' }
        Log 'Host HTTP preflight OK; identical XML.'
    } finally { $web.Dispose() }
    Mark 'READY' 'start'
    # Give the independent host connectivity probe time to reach the VM before phase A.
    $phases = @(
        @{ Name='READY'; Location=''; Duration=3.0 },
        @{ Name='A'; Location="http://$hostIp`:$($config.HttpPort)/device/description.xml"; Duration=15.0 },
        @{ Name='GAP'; Location=''; Duration=2.0 },
        @{ Name='B'; Location="http://$guestIp`:$($config.HttpPort)/device/description.xml"; Duration=15.0 },
        @{ Name='DRAIN'; Location=''; Duration=3.0 }
    )
    foreach ($phase in $phases) {
        Mark $phase.Name 'start'
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $rounds = @(0.0, 0.3, 0.6, 5.0, 10.0)
        $round = 0
        while ($clock.Elapsed.TotalSeconds -lt $phase.Duration) {
            if ($phase.Location -and $round -lt $rounds.Count -and $clock.Elapsed.TotalSeconds -ge $rounds[$round]) {
                Advertise 'alive' $phase.Location
                Log "$($phase.Name) alive round $($round + 1), LOCATION=$($phase.Location)"
                $round++
            }
            if ($listener.Pending()) {
                $client = $listener.AcceptTcpClient()
                try {
                    $stream = $client.GetStream()
                    $stream.ReadTimeout = 1200
                    $stream.WriteTimeout = 1200
                    $buffer = New-Object IO.MemoryStream
                    try {
                        while ($buffer.Length -lt 16384) {
                            $next = $stream.ReadByte()
                            if ($next -lt 0) { break }
                            $buffer.WriteByte([byte]$next)
                            $data = $buffer.GetBuffer()
                            $n = [int]$buffer.Length
                            if ($n -ge 4 -and $data[$n-4] -eq 13 -and $data[$n-3] -eq 10 -and $data[$n-2] -eq 13 -and $data[$n-1] -eq 10) { break }
                        }
                        $headers = [Text.Encoding]::ASCII.GetString($buffer.ToArray())
                    } finally { $buffer.Dispose() }
                    $request = ($headers -split "`r`n")[0]
                    $agent = (($headers -split "`r`n") | Where-Object { $_ -like 'User-Agent:*' }) -join ' '
                    Log "HTTP from $($client.Client.RemoteEndPoint): $request $agent"
                    $path = ($request -split ' ')[1]
                    $status = '200 OK'
                    if ($path -eq '/device/description.xml') { $body = $xml }
                    elseif ($path -like '/device/*.xml') {
                        $body = [Text.Encoding]::UTF8.GetBytes("<scpd xmlns='urn:schemas-upnp-org:service-1-0'><specVersion><major>1</major><minor>0</minor></specVersion><actionList/><serviceStateTable><stateVariable sendEvents='no'><name>TestState</name><dataType>string</dataType></stateVariable></serviceStateTable></scpd>")
                    } else { $status = '404 Not Found'; $body = [Text.Encoding]::ASCII.GetBytes('not found') }
                    $response = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: text/xml; charset=utf-8`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
                    $stream.Write($response, 0, $response.Length)
                    $stream.Write($body, 0, $body.Length)
                } catch { Log "HTTP client error: $($_.Exception.Message)" } finally { $client.Dispose() }
            }
            Start-Sleep -Milliseconds 20
        }
        if ($phase.Location) { Advertise 'byebye' ''; Mark $phase.Name 'end' }
    }
    Mark 'COMPLETE' 'end'
    Log 'Done. Send vm-result.log to the host operator. No settings were changed.'
} finally {
    if ($active -and $sender) { try { Advertise 'byebye' '' } catch {} }
    if ($listener) { $listener.Stop() }
    if ($sender) { $sender.Dispose() }
    [IO.File]::WriteAllLines($logPath, $log, (New-Object Text.UTF8Encoding($false)))
}
