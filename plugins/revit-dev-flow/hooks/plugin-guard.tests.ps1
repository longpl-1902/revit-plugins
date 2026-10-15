# plugin-guard.tests.ps1 - kiem hook plugin-guard.ps1 voi cac lenh mau. PS 5.1 / pwsh, chi ASCII.
# Chay: powershell -File hooks/plugin-guard.tests.ps1   (exit 0 = dat, 1 = co ca sai)
$ErrorActionPreference = 'Stop'
$hook = Join-Path $PSScriptRoot 'plugin-guard.ps1'
$exe = if (Get-Command powershell.exe -ErrorAction SilentlyContinue) { 'powershell.exe' } else { 'pwsh' }

# repo gia: Revit (2 kieu viet profiles), AutoCAD, khong co hawee.project.yaml
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('guard-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$repos = @{
    revit = "project: a`nprofiles: [revit-addin]`n"; revitList = "project: a`nprofiles:`n  - revit-addin  # chinh`n"
    autocad = "project: a`nprofiles: [autocad-addin]`n"; none = $null
}
foreach ($k in $repos.Keys) {
    New-Item -ItemType Directory -Force -Path (Join-Path $tmp "$k/src") | Out-Null
    if ($repos[$k]) { Set-Content -LiteralPath (Join-Path $tmp "$k/hawee.project.yaml") -Value $repos[$k] -Encoding UTF8 }
}
$env:CLAUDE_PROJECT_DIR = ''
$revit = Join-Path $tmp 'revit'

function Invoke-Guard([string]$tool, $toolInput, [string]$cwd = $revit) {
    $json = @{ tool_name = $tool; tool_input = $toolInput; cwd = $cwd } | ConvertTo-Json -Compress -Depth 4
    $out = $json | & $exe -NoProfile -NonInteractive -File $hook
    if (-not $out) { return 'none' }
    return (($out | Out-String) | ConvertFrom-Json).hookSpecificOutput.permissionDecision
}

$cases = @(
    @('Bash', 'git commit -m "x"', 'ask'),
    @('PowerShell', 'git.exe commit -m x', 'ask'),
    @('PowerShell', "& 'C:\Program Files\Git\cmd\git.exe' commit -m x", 'ask'),
    @('Bash', 'cmd /c "git commit -m x"', 'ask'),
    @('Bash', 'cd repo && git -C sub -c user.name=a push origin main', 'ask'),
    @('Bash', 'git push origin +main', 'deny'),
    @('Bash', 'git push --force origin main', 'deny'),
    @('Bash', 'git push -f', 'deny'),
    @('Bash', 'git push --force-with-lease', 'deny'),
    @('Bash', 'git commit --no-verify -m x', 'deny'),
    @('Bash', 'git status', 'none'),
    @('Bash', 'git log --grep commit', 'none'),
    @('Bash', 'echo push commit', 'none'),
    @('Bash', 'gitk --all', 'none')
)

$fail = 0
foreach ($c in $cases) {
    $got = Invoke-Guard $c[0] @{ command = $c[1] }
    if ($got -ne $c[2]) { $fail++; Write-Output ("SAI  {0,-5} -> {1,-5} : {2}" -f $c[2], $got, $c[1]) }
    else { Write-Output ("dat  {0,-5}          : {1}" -f $got, $c[1]) }
}
# pham vi: chi repo add-in Revit bi giu
$scope = @(
    @((Join-Path $tmp 'revit/src'), 'ask'), @((Join-Path $tmp 'revitList'), 'ask'),
    @((Join-Path $tmp 'autocad'), 'none'), @((Join-Path $tmp 'none/src'), 'none')
)
foreach ($c in $scope) {
    $got = Invoke-Guard 'Bash' @{ command = 'git commit -m x' } $c[0]
    if ($got -ne $c[1]) { $fail++; Write-Output ("SAI  {0,-5} -> {1,-5} : pham vi {2}" -f $c[1], $got, $c[0]) }
    else { Write-Output ("dat  {0,-5}          : pham vi {1}" -f $got, $c[0].Substring($tmp.Length)) }
}
$env:CLAUDE_PROJECT_DIR = $revit
$got = Invoke-Guard 'Bash' @{ command = 'git push' } (Join-Path $tmp 'none')
if ($got -ne 'ask') { $fail++; Write-Output "SAI  ask   -> $got : CLAUDE_PROJECT_DIR la repo Revit" } else { Write-Output 'dat  ask            : CLAUDE_PROJECT_DIR la repo Revit' }
$env:CLAUDE_PROJECT_DIR = ''
Remove-Item -LiteralPath $tmp -Recurse -Force

$g = Invoke-Guard 'mcp__plugin_revit-dev-flow_hawee-flow__gate_record' @{ gate = 'G1'; decision = 'PASS'; by = 'TL' }
if ($g -ne 'ask') { $fail++; Write-Output "SAI  gate_record -> $g" } else { Write-Output 'dat  ask            : gate_record' }

Write-Output ("{0} ca, {1} sai" -f ($cases.Count + $scope.Count + 2), $fail)
if ($fail -gt 0) { exit 1 }
exit 0
