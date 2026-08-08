# Mock hyveman backend for smoke testing. Logs every request to mock-requests.log
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:8443/")
$listener.Start()
$log = "C:\Dev\hyveman\.smoke\mock-requests.log"
"mock started $(Get-Date -Format o)" | Set-Content $log

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $req = $ctx.Request
    $body = ""
    if ($req.HasEntityBody) {
        $ms = New-Object System.IO.MemoryStream
        $req.InputStream.CopyTo($ms)
        $bytes = $ms.ToArray()
        if ($req.Headers['Content-Encoding'] -eq 'gzip') {
            $in = New-Object System.IO.MemoryStream(,$bytes)
            $gz = New-Object System.IO.Compression.GZipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
            $sr = New-Object System.IO.StreamReader($gz, [System.Text.Encoding]::UTF8)
            $body = $sr.ReadToEnd()
            $sr.Close(); $gz.Close(); $in.Close()
        } else {
            $body = [System.Text.Encoding]::UTF8.GetString($bytes)
        }
        $ms.Close()
    }
    $line = "[$(Get-Date -Format o)] $($req.HttpMethod) $($req.Url.AbsolutePath) auth=$($req.Headers['Authorization']) proto=$($req.Headers['X-Hyveman-Protocol']) source=$($req.Headers['X-Hyveman-Source']) enc=$($req.Headers['Content-Encoding']) len=$($body.Length)"
    Add-Content $log $line

    $resp = $ctx.Response
    $resp.StatusCode = 200
    $resp.ContentType = "application/json; charset=utf-8"
    $resp.Headers.Add("X-Hyveman-Protocol", "1")
    $json = ""
    switch ($req.Url.AbsolutePath) {
        "/register" {
            $json = '{"v":1,"source_id":"src_smoke01","token":"agt_smoketoken","scopes":["ingest"],"issued_at":"2024-08-07T15:02:11Z","commands":[]}'
        }
        "/ingest/logs" {
            $n = 0
            $first = ""
            try {
                $parsed = $body | ConvertFrom-Json
                $n = @($parsed.items).Count
                if ($n -gt 0) {
                    $item = @($parsed.items)[0]
                    $msg = "$($item.message)"
                    if ($msg.Length -gt 60) { $msg = $msg.Substring(0, 60) }
                    $first = "record_id=$($item.record_id) scope=$($item.dedup_scope) sev=$($item.severity) facility=$($item.facility) event_id=$($item.fields.event_id) time=$($item.time) msg=$msg"
                }
            } catch { $first = "parse-error: $($_.Exception.Message)" }
            $json = "{`"v`":1,`"accepted`":$n,`"deduped`":0,`"rejected`":[],`"commands`":[]}"
            Add-Content $log "  -> accepted $n items; first: $first"
        }
        "/ingest/telemetry" {
            $kinds = ""
            try {
                $parsed = $body | ConvertFrom-Json
                $kinds = (@($parsed.items) | ForEach-Object { $_.kind }) -join ","
            } catch {}
            $json = '{"v":1,"accepted":true,"commands":[]}'
            Add-Content $log "  -> telemetry items: $kinds"
        }
        "/health" {
            $json = '{"v":1,"ok":true,"server_time":"2024-08-07T15:02:11Z","server_version":"0.1.0","commands":[]}'
        }
        default {
            $resp.StatusCode = 404
            $json = '{"v":1,"error":{"code":"not_found","message":"no such route"},"commands":[]}'
        }
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $resp.OutputStream.Write($bytes, 0, $bytes.Length)
    $resp.Close()
}
