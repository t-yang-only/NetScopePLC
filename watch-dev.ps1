param(
    [string]$Root = $PSScriptRoot,
    [int]$DebounceMs = 1500
)

$devRun = Join-Path $Root "dev-run.bat"
$extensions = @(".cs", ".xaml", ".c")

Write-Host "[watch-dev] Watching $Root ($($extensions -join ', '))"
Write-Host "[watch-dev] Save -> wait ${DebounceMs}ms -> build + admin run"

function Test-SourceFile([string]$name) {
    $ext = [IO.Path]::GetExtension($name).ToLowerInvariant()
    return $extensions -contains $ext
}

function Invoke-BuildRun {
    $stamp = Get-Date -Format "HH:mm:ss"
    Write-Host "`n[$stamp] Rebuilding..."
    & $devRun
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[$stamp] dev-run failed (exit $LASTEXITCODE)" -ForegroundColor Red
    }
}

$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = $Root
$watcher.Filter = "*.*"
$watcher.IncludeSubdirectories = $true
$watcher.EnableRaisingEvents = $true

Invoke-BuildRun

while ($true) {
    $change = $watcher.WaitForChanged(
        [IO.WatcherChangeTypes]::Changed -bor
        [IO.WatcherChangeTypes]::Created -bor
        [IO.WatcherChangeTypes]::Renamed,
        1000)

    if ($change.TimedOut) { continue }
    if (-not (Test-SourceFile $change.Name)) { continue }

    Start-Sleep -Milliseconds $DebounceMs
    $watcher.WaitForChanged([IO.WatcherChangeTypes]::All, 300) | Out-Null

    Invoke-BuildRun
}
