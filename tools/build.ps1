$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$output = if ($args.Count -gt 0) { [IO.Path]::GetFullPath([string]$args[0], $repoRoot) } else { Join-Path $repoRoot 'artifacts/single-file' }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (!$output.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory must be within project artifacts.' }
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.cli'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.packages'
foreach ($tool in 'primer3/primer3-2.6.1/src/primer3_core.exe','primer3/primer3-2.6.1/src/ntthal.exe','blast/ncbi-blast-2.14.0+/bin/blastn.exe','reference/node.exe') {
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $tool))) { throw "Tool missing: $tool. Run setup-tools.ps1 first." }
}
dotnet restore (Join-Path $repoRoot 'QpcrPrimerStudio.sln') --locked-mode --configfile (Join-Path $repoRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
dotnet run --project (Join-Path $repoRoot 'tests/QpcrPrimerStudio.Checks') --no-restore -- $repoRoot
if ($LASTEXITCODE -ne 0) { throw 'Integration validation failed.' }
if (Test-Path -LiteralPath $output) {
    $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
    if ($resolvedOutput -ne $output) { throw 'Unexpected publish directory.' }
    $archive = Join-Path $repoRoot ('artifacts/package-history/' + [datetime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Path $archive -Force | Out-Null
    Move-Item -LiteralPath $resolvedOutput -Destination (Join-Path $archive 'single-file')
}
dotnet publish (Join-Path $repoRoot 'src/QpcrPrimerStudio.Desktop') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=embedded -p:NuGetLockFilePath=packages.publish.lock.json -o $output --configfile (Join-Path $repoRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (@(Get-ChildItem -LiteralPath $output -Recurse -File).Count -ne 1) { throw 'Publish did not produce exactly one file.' }
& (Join-Path $PSScriptRoot 'verify-package.ps1') $output
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $output
$files = Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.FullName -ne (Join-Path $output 'build-manifest.json') } | ForEach-Object {
    [pscustomobject]@{ Path=[IO.Path]::GetRelativePath($output,$_.FullName); Bytes=$_.Length }
}
$manifest = [pscustomobject]@{ BuiltAt=[datetimeoffset]::Now.ToString('O'); Framework='net10.0-windows'; RuntimeIdentifier='win-x64'; SingleFile=$true; SelfContained=$true; Compression=$true; Files=$files }
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Encoding UTF8
Write-Output "Ready: $(Join-Path $output 'QpcrPrimerStudio.exe')"
