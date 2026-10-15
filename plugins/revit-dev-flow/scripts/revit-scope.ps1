# revit-scope.ps1 - dot-source: Test-RevitRepo. Pham vi plugin revit-dev-flow = repo add-in Revit:
# tim thay hawee.project.yaml (tu thu muc dua vao di len) co profiles chua revit-addin. PS 5.1, chi ASCII.
# Cung dinh nghia voi ProjectContext.Open (src/Hawee.Flow.Core/Infra/ProjectContext.cs).
# Thu muc $start (hoac thu muc cha) co hawee.project.yaml voi profiles chua revit-addin?
# Doc 'profiles: [a, b]' hoac 'profiles:' + cac dong '  - a'.
function Test-RevitRepo([string]$start) {
    if (-not $start) { return $false }
    try { $dir = [System.IO.DirectoryInfo][System.IO.Path]::GetFullPath($start) } catch { return $false }
    while ($dir -and -not (Test-Path -LiteralPath (Join-Path $dir.FullName 'hawee.project.yaml'))) { $dir = $dir.Parent }
    if (-not $dir) { return $false }
    $lines = @(Get-Content -LiteralPath (Join-Path $dir.FullName 'hawee.project.yaml') -Encoding UTF8)
    $profiles = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^profiles\s*:\s*(.*)$') { continue }
        $v = ($Matches[1] -replace '\s+#.*$', '').Trim()
        if ($v -match '^\[(.*)\]$') { $profiles = $Matches[1] -split ',' }
        elseif ($v) { $profiles = @($v) }
        else { for ($j = $i + 1; $j -lt $lines.Count -and $lines[$j] -match '^\s+-\s*(.+)$'; $j++) { $profiles += $Matches[1] -replace '\s+#.*$', '' } }
        break
    }
    foreach ($p in $profiles) { if ($p.Trim().Trim('"', "'") -eq 'revit-addin') { return $true } }
    return $false
}
