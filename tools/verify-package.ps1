$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$packageDirectory = if ($args.Count -gt 0) { [IO.Path]::GetFullPath([string]$args[0], $repoRoot) } else { Join-Path $repoRoot 'artifacts/single-file' }
$source = Join-Path $packageDirectory 'QpcrPrimerStudio.exe'
if (!(Test-Path -LiteralPath $source)) { throw 'Single-file executable is missing.' }
$work = Join-Path $repoRoot ('artifacts/package-checks/' + [datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$executable = Join-Path $work 'QpcrPrimerStudio.exe'
Copy-Item -LiteralPath $source -Destination $executable
$report = Join-Path $work 'verification.json'
$previousExtractionRoot = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $work 'bundle-cache'
    $process = Start-Process -FilePath $executable -ArgumentList @('--verify-package',('"' + $report + '"')) -WorkingDirectory $work -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id
        throw 'Standalone package verification timed out.'
    }
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Encoding UTF8 | Write-Output }
        throw "Standalone package exited with code $($process.ExitCode)."
    }
    $result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$result.IndependentReauditRepairs) { throw 'Independent re-audit package verification failed.' }
    if (!$result.SmartBindingFilters) { throw 'Smart-export binding filter verification failed.' }
    if (!$result.Success -or !$result.Icon -or !$result.Window -or !$result.SQLite -or !$result.Thermodynamics -or !$result.RuntimeBundled -or !$result.SelectionAutoSave -or !$result.FastaImport -or !$result.GenBankImport -or !$result.ParameterRecommendation -or !$result.ProjectParameterSharing -or !$result.SmartExport -or !$result.TargetNavigation -or !$result.CheckedOnlyDefault -or !$result.AuditRepairs) { throw 'Package verification failed.' }
    Write-Output "Standalone package verified: $report"
} finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtractionRoot
}
