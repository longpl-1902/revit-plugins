# plugin-guard.ps1 - hook PreToolUse cua plugin revit-dev-flow (PS 5.1, chi ASCII).
#   gate_record (hawee-flow)        -> "ask": chi nguoi duyet ghi quyet dinh cong G1-G6
#   git commit / git push (Bash, PowerShell) -> "ask": G4 - chu task cho phep cho LAN NAY
#   git push --force / --no-verify  -> "deny"
# Pham vi: plugin chi dung khi phat trien add-in Revit. Lenh git chi bi giu khi cwd (JSON hook) hoac CLAUDE_PROJECT_DIR
# nam trong repo add-in Revit = tim thay hawee.project.yaml di len co profiles chua revit-addin. Repo khac -> khong can thiep.
# Loi bat ngo -> chan (exit 2), khong de loi goi di qua.
$ErrorActionPreference = 'Stop'
trap { [Console]::Error.WriteLine("[revit-dev-flow] chan: loi trong hook - $($_.Exception.Message)"); exit 2 }

function Decide([string]$decision, [string]$reason) {
    $o = @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = $decision; permissionDecisionReason = $reason } }
    [Console]::Out.Write(($o | ConvertTo-Json -Compress -Depth 4))
    exit 0
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/revit-scope.ps1')

$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
$hook = $raw | ConvertFrom-Json
$tool = [string]$hook.tool_name
$ti = $hook.tool_input

if ($tool -match 'hawee-flow__gate_record$') {
    $g = [string]$ti.gate; $d = [string]$ti.decision; $by = [string]$ti.by
    Decide 'ask' ("Cong ${g}: ghi quyet dinh $d boi '$by'. Chi dong y neu nguoi duyet that su da quyet dinh nhu vay.")
}

if ($tool -eq 'Bash' -or $tool -eq 'PowerShell') {
    $cwd = if ($hook.PSObject.Properties['cwd']) { [string]$hook.cwd } else { '' }
    if (-not ((Test-RevitRepo $cwd) -or (Test-RevitRepo $env:CLAUDE_PROJECT_DIR))) { exit 0 }
    $cmd = [string]$ti.command
    # git / git.exe / "C:\...\git.exe" / cmd /c "git ...", kem tuy chon toan cuc (-C dir, -c k=v)
    if ($cmd -match '(^|[;&|(\s"''`\\/])git(\.exe)?["'']?(\s+-[^\s]+(\s+[^\s-][^\s]*)?)*\s+(commit|push)\b') {
        if ($cmd -match '\bpush\b.*(\s--force\b|\s-f\b|\s--force-with-lease\b|\s--force-if-includes\b|\s\+[^\s]+)') { Decide 'deny' 'G4: khong force-push (ke ca refspec +nhanh).' }
        if ($cmd -match '--no-verify') { Decide 'deny' 'Khong bo qua hook git (--no-verify).' }
        Decide 'ask' 'G4: chu task phai noi ro "commit"/"push" cho LAN NAY (lan duyet truoc khong ap dung).'
    }
}
exit 0
