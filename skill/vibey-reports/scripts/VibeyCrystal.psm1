# VibeyCrystal.psm1
function Invoke-VibeyRead  { param([Parameter(Mandatory)]$Request) @{ ok = $false; error = 'read not implemented' } }
function Invoke-VibeyApply { param([Parameter(Mandatory)]$Request) @{ ok = $false; error = 'apply not implemented' } }
Export-ModuleMember -Function Invoke-VibeyRead, Invoke-VibeyApply
