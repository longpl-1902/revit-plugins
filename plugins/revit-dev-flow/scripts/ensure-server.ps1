# ensure-server.ps1 - SessionStart: dung (dotnet publish) server hawee-flow vao $CLAUDE_PLUGIN_DATA\server
# khi chua co hoac ma nguon plugin da doi. Im lang khi khong co gi phai lam (stdout SessionStart vao context).
# Chay tay: powershell -File scripts/ensure-server.ps1 [-Force]. PS 5.1, chi ASCII.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$root = if ($env:CLAUDE_PLUGIN_ROOT) { $env:CLAUDE_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
$data = if ($env:CLAUDE_PLUGIN_DATA) { $env:CLAUDE_PLUGIN_DATA } else { Join-Path $env:LOCALAPPDATA 'hawee-flow' }
$src = Join-Path $root 'src'
$out = Join-Path $data 'server'
$stampFile = Join-Path $out '.stamp'

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
    Write-Output "[revit-dev-flow] Chua co .NET 8 SDK (lenh dotnet) - server hawee-flow chua dung duoc. Cai .NET 8 SDK roi mo phien moi."
    exit 0
}
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $data 'build.log'
$proj = Join-Path $src 'Hawee.Flow.Server\Hawee.Flow.Server.csproj'
& dotnet publish $proj -c Release -o $out --nologo -v q *> $log
if ($LASTEXITCODE -ne 0) {
    Write-Output "[revit-dev-flow] Dung server hawee-flow loi (xem $log). Tool hawee-flow chua dung duoc trong phien nay."
    exit 0
}
Set-Content -LiteralPath $stampFile -Value $stamp -Encoding ASCII
Write-Output "[revit-dev-flow] Da dung lai server hawee-flow. Neu tool hawee-flow chua hien, go /mcp de ket noi lai."
exit 0
