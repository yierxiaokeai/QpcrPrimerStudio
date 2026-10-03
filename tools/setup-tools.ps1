$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
$repoRoot = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $repoRoot ('artifacts/tool-setup/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
function Get-Verified([string]$url, [string]$name, [string]$sha) {
    $path = Join-Path $stage $name
    $cacheNames = @{ 'primer3.zip'='primer3-2.6.1.zip'; 'gcc.pkg.tar.zst'='gcc-libs.pkg.tar.zst'; 'pthread.pkg.tar.zst'='winpthread.pkg.tar.zst'; 'ugene.zip'='ugene-53.1.zip'; 'npm.tgz'='npm-11.6.1.tgz' }
    $cached = if ($cacheNames.ContainsKey($name)) { Join-Path $repoRoot ('artifacts/downloads/' + $cacheNames[$name]) } else { $null }
    if ($cached -and (Test-Path -LiteralPath $cached)) { Copy-Item -LiteralPath $cached -Destination $path }
    else { Invoke-WebRequest -Uri $url -OutFile $path }
    if ($sha -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $sha) { throw "Checksum mismatch: $name" }
    return $path
}
function Install-File([string]$source, [string]$destination) {
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $source).Hash) { throw "Existing tool differs; preserve and inspect it before replacing: $destination" }
    } else { Copy-Item -LiteralPath $source -Destination $destination }
}
$primerArchive = Get-Verified 'https://github.com/primer3-org/primer3/releases/download/v2.6.1/primer3-2.6.1_exe_for_windows.zip' 'primer3.zip' 'FD1333E816866608580651566F1E4556EAA886B23F3812440B18A9B6C983BF01'
$primerStage = Join-Path $stage 'primer3'
Expand-Archive -LiteralPath $primerArchive -DestinationPath $primerStage
$primerSource = Join-Path $primerStage 'primer3-2.6.1'
$primerTarget = Join-Path $PSScriptRoot 'primer3/primer3-2.6.1'
Install-File (Join-Path $primerSource 'LICENSE') (Join-Path $primerTarget 'LICENSE')
foreach ($name in 'primer3_core.exe','ntthal.exe') { Install-File (Join-Path $primerSource "src/$name") (Join-Path $primerTarget "src/$name") }
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $primerSource 'src/primer3_config') -File -Recurse) {
    Install-File $file.FullName (Join-Path $primerTarget ('src/primer3_config/' + [IO.Path]::GetRelativePath((Join-Path $primerSource 'src/primer3_config'), $file.FullName)))
}
$gcc = Get-Verified 'https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-gcc-libs-15.2.0-14-any.pkg.tar.zst' 'gcc.pkg.tar.zst' '717F699E690374360764A083EB5CEEEF495B54A22ED81D52CB5C714AAE3FBAF8'
$pthread = Get-Verified 'https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-libwinpthread-14.0.0.r420.g61d40c4c0-1-any.pkg.tar.zst' 'pthread.pkg.tar.zst' 'C3FF34E309F785D5BF8C94015934E9AA5C058ED2ADAD58C5456540DD639EB09A'
$runtimeStage = Join-Path $stage 'runtime'
New-Item -ItemType Directory -Path $runtimeStage | Out-Null
foreach ($archive in $gcc,$pthread) { tar -xf $archive -C $runtimeStage; if ($LASTEXITCODE -ne 0) { throw 'Runtime extraction failed.' } }
foreach ($name in 'libgcc_s_seh-1.dll','libstdc++-6.dll','libwinpthread-1.dll') { Install-File (Join-Path $runtimeStage "mingw64/bin/$name") (Join-Path $primerTarget "src/$name") }
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $runtimeStage 'mingw64/share/licenses') -File -Recurse) {
    Install-File $file.FullName (Join-Path $primerTarget ('src/runtime-licenses/' + [IO.Path]::GetRelativePath((Join-Path $runtimeStage 'mingw64/share/licenses'), $file.FullName)))
}
$ugene = Get-Verified 'https://github.com/ugeneunipro/ugene/releases/download/53.1/ugene-53.1-win-x86-64.zip' 'ugene.zip' '71BE27B6CE4100347C2C7A851E62ACE84ABDDDD1D79BFB0BB8AB89DFBF81E9FE'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($ugene)
try {
    foreach ($entry in $zip.Entries | Where-Object { $_.FullName -match '^ugene-53\.1/tools/blast/(blastn\.exe|blastdbcmd\.exe|makeblastdb\.exe|[^/]+\.dll)$' }) {
        $temporary = Join-Path $stage ([IO.Path]::GetFileName($entry.FullName))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $temporary)
        Install-File $temporary (Join-Path $PSScriptRoot ('blast/ncbi-blast-2.14.0+/bin/' + [IO.Path]::GetFileName($entry.FullName)))
    }
    $temporary = Join-Path $stage 'UGENE-LICENSE.txt'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('ugene-53.1/LICENSE.3rd_party.txt'), $temporary)
    Install-File $temporary (Join-Path $PSScriptRoot 'blast/ncbi-blast-2.14.0+/UGENE-LICENSE.txt')
} finally { $zip.Dispose() }
foreach ($source in @(
    @{ Url='https://raw.githubusercontent.com/ncbi/ncbi-cxx-toolkit-public/master/LICENSE'; Name='blast/ncbi-blast-2.14.0+/NCBI-LICENSE' },
    @{ Url='https://raw.githubusercontent.com/nghttp2/nghttp2/master/COPYING'; Name='blast/ncbi-blast-2.14.0+/NGHTTP2-LICENSE' },
    @{ Url='https://raw.githubusercontent.com/nodejs/node/v24.19.0/LICENSE'; Name='reference/NODE-LICENSE' })) {
    $destination = Join-Path $PSScriptRoot $source.Name
    if (!(Test-Path -LiteralPath $destination)) { New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null; Invoke-WebRequest -Uri $source.Url -OutFile $destination }
}
$nodeTarget = Join-Path $PSScriptRoot 'reference/node.exe'
if (!(Test-Path -LiteralPath $nodeTarget)) {
    $node = Get-Verified 'https://nodejs.org/dist/v24.19.0/node-v24.19.0-win-x64.zip' 'node.zip' ''
    $nodeStage = Join-Path $stage 'node'
    Expand-Archive -LiteralPath $node -DestinationPath $nodeStage
    $nodeSource = Join-Path $nodeStage 'node-v24.19.0-win-x64/node.exe'
    if ((Get-FileHash -LiteralPath $nodeSource).Hash -ne '3602F2BB1A10F2CBAB4C36886218A33C1AB3DB87290E73B033C46C77147D0237') { throw 'Node executable checksum mismatch.' }
    Install-File $nodeSource $nodeTarget
}
$npm = Get-Verified 'https://registry.npmjs.org/npm/-/npm-11.6.1.tgz' 'npm.tgz' 'C8AC3E94B6809E36EB3EAC225C60BFED96784F49D03E248F7877AC13FB089725'
$npmStage = Join-Path $stage 'npm'
New-Item -ItemType Directory -Path $npmStage | Out-Null
tar -xf $npm -C $npmStage
if ($LASTEXITCODE -ne 0) { throw 'npm extraction failed.' }
& $nodeTarget (Join-Path $npmStage 'package/bin/npm-cli.js') install --prefix (Join-Path $PSScriptRoot 'reference') --ignore-scripts --no-audit --no-fund --cache (Join-Path $repoRoot '.cli/npm-cache')
if ($LASTEXITCODE -ne 0) { throw 'Parser dependency installation failed.' }
& (Join-Path $primerTarget 'src/primer3_core.exe') --about
if ($LASTEXITCODE -ne 0) { throw 'Primer3 startup failed.' }
& (Join-Path $PSScriptRoot 'blast/ncbi-blast-2.14.0+/bin/blastn.exe') -version
if ($LASTEXITCODE -ne 0) { throw 'BLAST startup failed.' }
Write-Output 'Tools ready. Download and extraction evidence remains in artifacts/tool-setup.'
