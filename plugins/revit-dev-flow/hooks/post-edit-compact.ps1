# post-edit-compact.ps1 - hook PostToolUse (Edit|Write|MultiEdit) cua plugin revit-dev-flow, ban GON cua
# BIM_KB_Base/core/tools/hooks/post-edit-check.ps1: cung lint (csproj-twin-lint / doc-lint / code-lint) nhung chi tra
# toi da 8 dong loi (ban goc tra 40 dong cuoi). Chi chay khi HAWEE_FLOW_POST_EDIT=1 - dat bien nay va BO hook goc
# trong .claude/settings.json de khong chay hai lan. PS 5.1, chi ASCII.
$ErrorActionPreference = 'Continue'
if ($env:HAWEE_FLOW_POST_EDIT -ne '1' -or $env:HAWEE_SKIP_POST_EDIT -eq '1') { exit 0 }
try { $hook = [Console]::In.ReadToEnd() | ConvertFrom-Json } catch { exit 0 }
$path = $null
foreach ($k in @('file_path', 'path')) { $p = $hook.tool_input.PSObject.Properties[$k]; if ($p -and $p.Value) { $path = [string]$p.Value; break } }
if (-not $path -or -not (Test-Path -LiteralPath $path)) { exit 0 }
$projectDir = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
$tools = Join-Path $projectDir 'BIM_KB_Base\core\tools'
if (-not (Test-Path -LiteralPath $tools)) { exit 0 }
$ext = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
$script = $null; $lintArgs = @()
switch ($ext) {
    '.csproj' { $script = 'csproj-twin-lint.ps1' }
    '.md' {
        $first = (Get-Content -LiteralPath $path -TotalCount 3 -Encoding UTF8) -join "`n"
        if ($first -match '^#\s+(Spec|Plan|Handoff)\s') { $script = 'doc-lint.ps1'; $lintArgs = @('-Path', $path) }
    }
    '.cs' { $script = 'code-lint.ps1'; $lintArgs = @('-Path', $path) }
}
if (-not $script) { exit 0 }
$out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tools $script) -Root $projectDir @lintArgs 2>&1 | Out-String
if ($LASTEXITCODE -eq 0) { exit 0 }
$name = $script.Replace('.ps1', '')
$keep = New-Object System.Collections.Generic.List[string]
$skip = $false
foreach ($line in ($out -split "`r?`n")) {
    if (-not $line.Trim()) { continue }
    if ($line -match '^\s') { if (-not $skip) { $keep.Add($line.Trim()) }; continue }
    $t = $line.Trim()
    if ($t.StartsWith("[$name]") -or $t.StartsWith('WARN') -or $t.StartsWith('warn ') -or $t.StartsWith('info ')) { $skip = $true; continue }
    $skip = $false; $keep.Add($t)
}
[Console]::Error.WriteLine("[$name] loi sau khi sua $([System.IO.Path]::GetFileName($path)):")
$keep | Select-Object -First 8 | ForEach-Object { [Console]::Error.WriteLine("  $_") }
if ($keep.Count -gt 8) { [Console]::Error.WriteLine("  ... +$($keep.Count - 8) dong (chay lai lint de xem het)") }
exit 2
