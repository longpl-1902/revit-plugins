# ensure-server.ps1 - SessionStart: dung (dotnet publish) server hawee-flow vao $CLAUDE_PLUGIN_DATA\server
# khi chua co hoac ma nguon plugin da doi. Im lang khi khong co gi phai lam (stdout SessionStart vao context).
# Chay tay: powershell -File scripts/ensure-server.ps1 [-Force]. PS 5.1, chi ASCII.
# Pham vi: chay tu hook (co CLAUDE_PROJECT_DIR) o repo khong phai add-in Revit -> thoat im lang, khong dung server.
#
# Claude Code mo MCP server song song voi hook nay -> server CU co the dang chay va giu khoa DLL trong server\.
# Vi vay: publish vao server.next, roi chep tung file sang server\; file dich dang bi nap thi doi ten
# (<ten>.old-<ticks>, Windows cho doi ten file dang nap) roi chep ban moi. .stamp ghi sau cung.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'revit-scope.ps1')
if (-not $Force -and $env:CLAUDE_PROJECT_DIR -and -not (Test-RevitRepo $env:CLAUDE_PROJECT_DIR)) { exit 0 }
$root = if ($env:CLAUDE_PLUGIN_ROOT) { $env:CLAUDE_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
# Chay tay (khong co CLAUDE_PLUGIN_DATA): tim thu muc data ma Claude Code dung (~\.claude\plugins\data\*revit-dev-flow*),
# vi .mcp.json chay ${CLAUDE_PLUGIN_DATA}\server - dung nham cho khac thi Claude Code van chay ban cu.
$data = $env:CLAUDE_PLUGIN_DATA
if (-not $data) {
    $pdata = Join-Path $HOME '.claude\plugins\data'
    $cands = @()
    if (Test-Path -LiteralPath $pdata) { $cands = @(Get-ChildItem -LiteralPath $pdata -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*revit-dev-flow*' }) }
    if ($cands.Count -eq 1) { $data = $cands[0].FullName }
    elseif ($cands.Count -gt 1) { Write-Output ("[revit-dev-flow] Nhieu thu muc data: " + (($cands | ForEach-Object { $_.FullName }) -join ', ') + " - dat CLAUDE_PLUGIN_DATA=<thu muc dung> roi chay lai."); exit 0 }
    else { $data = Join-Path $env:LOCALAPPDATA 'hawee-flow'; Write-Output "[revit-dev-flow] Khong thay ~\.claude\plugins\data\*revit-dev-flow* - dung vao $data (Claude Code chi dung ban nay neu CLAUDE_PLUGIN_DATA tro toi day)." }
}
$src = Join-Path $root 'src'
$out = Join-Path $data 'server'
$next = Join-Path $data 'server.next'
$stampFile = Join-Path $out '.stamp'
$log = Join-Path $data 'build.log'
function Say([string]$m) { Write-Output "[revit-dev-flow] $m" }

# don file cu da doi ten o lan dung truoc (file con bi khoa thi de lan sau)
if (Test-Path -LiteralPath $out) {
    Get-ChildItem -LiteralPath $out -Recurse -File -Filter '*.old-*' -ErrorAction SilentlyContinue |
        ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop } catch { } }
}

# dau van tay ma nguon: ten + kich thuoc + thoi gian sua cua moi file .cs/.csproj/.yaml/.props
$files = Get-ChildItem -LiteralPath $src -Recurse -File -Include *.cs, *.csproj, *.yaml, *.props |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.FullName -notmatch 'Hawee\.Flow\.Tests' } |
    Sort-Object FullName
$sb = New-Object System.Text.StringBuilder
foreach ($f in $files) { [void]$sb.Append($f.FullName.Substring($src.Length)).Append('|').Append($f.Length).Append('|').Append($f.LastWriteTimeUtc.Ticks).Append("`n") }
$sha = [System.Security.Cryptography.SHA256]::Create()
$stamp = [System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sb.ToString()))).Replace('-', '')

$dll = Join-Path $out 'Hawee.Flow.Server.dll'
if (-not $Force -and (Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $stampFile) -and ((Get-Content -LiteralPath $stampFile -Raw).Trim() -eq $stamp)) { exit 0 }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Say "Chua co .NET 8 SDK (lenh dotnet) - server hawee-flow chua dung duoc. Cai .NET 8 SDK roi mo phien moi."
    exit 0
}

# 1. publish vao thu muc tam (khong dung toi server\ dang chay)
New-Item -ItemType Directory -Force -Path $data | Out-Null
if (Test-Path -LiteralPath $next) { Remove-Item -LiteralPath $next -Recurse -Force }
$proj = Join-Path $src 'Hawee.Flow.Server\Hawee.Flow.Server.csproj'
& dotnet publish $proj -c Release -o $next --nologo -v q *> $log
if ($LASTEXITCODE -ne 0) {
    Say "Dung server hawee-flow loi (exit $LASTEXITCODE, log: $log). Tool hawee-flow chua dung duoc trong phien nay:"
    $lines = @(Select-String -LiteralPath $log -Pattern '\berror\b' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique -First 5)
    if ($lines.Count -eq 0) { $lines = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Where-Object { $_.Trim() } | Select-Object -Last 5) }
    foreach ($l in $lines) { Write-Output "  $l" }
    exit 0
}

# 2. chep sang server\; file dang bi khoa -> doi ten roi chep
New-Item -ItemType Directory -Force -Path $out | Out-Null
$nextFull = (Get-Item -LiteralPath $next).FullName
$locked = @(); $renamed = 0
foreach ($f in Get-ChildItem -LiteralPath $nextFull -Recurse -File) {
    $rel = $f.FullName.Substring($nextFull.Length).TrimStart('\', '/')
    $dst = Join-Path $out $rel
    $dir = Split-Path -Parent $dst
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    try { Copy-Item -LiteralPath $f.FullName -Destination $dst -Force -ErrorAction Stop; continue } catch { }
    try {
        Rename-Item -LiteralPath $dst -NewName ((Split-Path -Leaf $dst) + '.old-' + [DateTime]::UtcNow.Ticks) -ErrorAction Stop
        Copy-Item -LiteralPath $f.FullName -Destination $dst -Force -ErrorAction Stop
        $renamed++
    }
    catch { $locked += $rel }
}
if ($locked.Count -gt 0) {
    Say ("Da dung server moi (" + $next + ") nhung khong thay duoc file dang bi khoa: " + (($locked | Select-Object -First 5) -join ', ') + ".")
    Say "Dong cac phien Claude Code khac (hoac dung tien trinh dotnet chay Hawee.Flow.Server.dll) roi chay: powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Force"
    exit 0
}
try { Remove-Item -LiteralPath $next -Recurse -Force -ErrorAction Stop } catch { }
Set-Content -LiteralPath $stampFile -Value $stamp -Encoding ASCII
if ($renamed -gt 0) { Say "Da dung lai server hawee-flow (server cu dang chay). Go /mcp de ket noi lai ban moi." }
else { Say "Da dung lai server hawee-flow. Neu tool hawee-flow chua hien, go /mcp de ket noi lai." }
exit 0
