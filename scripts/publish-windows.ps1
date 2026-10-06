$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'windows-app\UsageMonitor.Windows\UsageMonitor.Windows.csproj'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK gerekli. https://dotnet.microsoft.com/download/dotnet/10.0 adresinden kurun.'
}
$sdkLines = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet SDK bilgisi okunamadı.'
}
if (-not ($sdkLines | Where-Object { $_ -match '^10\.' })) {
    throw '.NET 10 SDK gerekli. https://dotnet.microsoft.com/download/dotnet/10.0 adresinden kurun.'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactRoot = Join-Path $repoRoot 'artifacts'
$publishDirectory = Join-Path $artifactRoot "publish-win-x64-$stamp"
$archivePath = Join-Path $artifactRoot "CodexUsageMonitor-win-x64-$stamp.zip"
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'Windows x64 derlemesi başarısız oldu.'
}

Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
Write-Output "Paket hazır: $archivePath"
