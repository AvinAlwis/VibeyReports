$entry = Join-Path $PSScriptRoot '..\vibey-reports\scripts\vibey.ps1'

It 'returns ok:false with a message for malformed JSON' {
    $out = 'not json at all' | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $entry
    $r = $out -join '' | ConvertFrom-Json
    Should-Be $r.ok $false 'ok'
    Should-Contain $r.error 'JSON'
}

It 'returns ok:false for an unknown command' {
    $out = '{"command":"explode"}' | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $entry
    $r = $out -join '' | ConvertFrom-Json
    Should-Be $r.ok $false 'ok'
    Should-Contain $r.error 'explode'
}

It 'writes stdout with no byte order mark' {
    $tmp = [IO.Path]::GetTempFileName()
    '{"command":"explode"}' | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $entry |
        Out-File -LiteralPath $tmp -Encoding utf8
    $bytes = [IO.File]::ReadAllBytes($tmp)
    Remove-Item $tmp -Force
    if ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw 'stdout began with a UTF-8 BOM'
    }
}
