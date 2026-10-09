param([string]$Output = '.artifacts/launcher-win-x64')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskCache = Join-Path $taskRoot '.artifacts'
# Keep build caches beside the repository, especially when the system disk is small.
$previousEnvironment = @{}
foreach ($name in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'TEMP', 'TMP')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location $taskRoot
try {
    foreach ($folder in @('nuget', 'nuget-http', 'temp')) { New-Item -ItemType Directory -Path (Join-Path $taskCache $folder) -Force | Out-Null }
    $env:NUGET_PACKAGES = Join-Path $taskCache 'nuget'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $taskCache 'nuget-http'
    $env:TEMP = Join-Path $taskCache 'temp'
    $env:TMP = $env:TEMP
    $fallback = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    $publishArguments = @('publish', 'src/Launcher.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Output, '-m:1')
    if (Test-Path -LiteralPath $fallback) { $publishArguments += "-p:RestoreFallbackFolders=$fallback" }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
finally {
    foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    Pop-Location
}
