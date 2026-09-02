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
    # MEASURED: "Out-File -Encoding utf8" ALWAYS prepends a BOM on Windows PowerShell 5.1,
    # whatever it is given - verified with a bare string. Routing the child's output through
    # it would therefore test Out-File, not vibey.ps1, and fail no matter what the program
    # emits. Read the child's raw stdout stream instead, before anything can re-encode it.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = 'powershell.exe'
    $psi.Arguments              = "-NoProfile -ExecutionPolicy Bypass -File `"$entry`""
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute        = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write('{"command":"explode"}')
    $p.StandardInput.Close()
    $bytes = New-Object byte[] 3
    $read  = $p.StandardOutput.BaseStream.Read($bytes, 0, 3)
    $p.WaitForExit()
    if ($read -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "stdout began with a UTF-8 BOM: $($bytes -join ',')"
    }
}
