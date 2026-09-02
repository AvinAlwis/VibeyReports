# VibeyCrystal.psm1
function Invoke-VibeyRead  { @{ ok = $false; error = 'read not implemented' } }
function Invoke-VibeyApply { @{ ok = $false; error = 'apply not implemented' } }
Export-ModuleMember -Function Invoke-VibeyRead, Invoke-VibeyApply
