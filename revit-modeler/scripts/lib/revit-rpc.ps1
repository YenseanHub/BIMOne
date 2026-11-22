# Minimal JSON-RPC client for the BIMOne fanmo socket service (raw JSON-RPC over TCP).
# Kept ASCII-only for Windows PowerShell 5.1 compatibility.

function Test-JsonComplete {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $false }
    $depth = 0
    $inString = $false
    $escape = $false
    $started = $false
    foreach ($ch in $Text.ToCharArray()) {
        if ($inString) {
            if ($escape) { $escape = $false }
            elseif ($ch -eq [char]'\') { $escape = $true }
            elseif ($ch -eq [char]'"') { $inString = $false }
            continue
        }
        if ($ch -eq [char]'"') { $inString = $true; continue }
        if ($ch -eq [char]'{') { $depth++; $started = $true }
        elseif ($ch -eq [char]'}') { $depth-- }
    }
    return ($started -and $depth -eq 0 -and -not $inString)
}

function Invoke-FanmoRpc {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [hashtable]$Params = @{},
        [int]$Port = 8982,
        [int]$TimeoutMs = 120000
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect("127.0.0.1", $Port)
    } catch {
        throw "Cannot connect to 127.0.0.1:$Port - is the fanmo service switched on in Revit?"
    }

    try {
        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMs
        $stream.WriteTimeout = $TimeoutMs

        $payload = @{ jsonrpc = "2.0"; id = "1"; method = $Method; params = $Params } |
            ConvertTo-Json -Depth 30 -Compress
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()

        $builder = New-Object System.Text.StringBuilder
        $buffer = New-Object byte[] 16384

        while ($true) {
            $read = $stream.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) { break }
            [void]$builder.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $read))
            if (Test-JsonComplete $builder.ToString()) { break }
        }

        return $builder.ToString()
    } finally {
        $client.Close()
    }
}
