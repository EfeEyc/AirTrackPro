$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "Could not find .NET Framework C# compiler at $csc"
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outExe = Join-Path $scriptDir "AirTrackBridge.exe"
$sources = Get-ChildItem -Path (Join-Path $scriptDir "src") -Filter "*.cs" -Recurse | ForEach-Object { $_.FullName }

& $csc /nologo /optimize+ /platform:x64 /target:exe /out:$outExe $sources
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built $outExe successfully."
