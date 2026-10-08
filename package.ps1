param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'outputs'))
$ErrorActionPreference='Stop'
$binaryRoot=Join-Path $PSScriptRoot 'bin'
$exePath=Join-Path $binaryRoot 'autocat.exe'
if(!(Test-Path -LiteralPath $exePath -PathType Leaf)){throw 'Missing release binary: autocat.exe'}
# Resource names live in the UTF-8 metadata heap, not the UTF-16 heap C# string literals use. The exe's --self-test
# checks that the embedded name matches its own RuntimeId exactly.
$runtime=[regex]::Match([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($exePath)),'AutoCat\.Runtime\.autocat\.runtime\.(\d+)\.dll')
if(!$runtime.Success){throw 'Executable is missing the embedded runtime resource.'}
# A dry-run build (build.ps1 -PawPassDryRun) is for observation only and is never released.
if([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($exePath)).Contains('PawPassDryRunBuild')){throw 'Executable is a PawPass dry-run build and is not packaged. Rebuild without -PawPassDryRun.'}
# A leftover executable from an earlier build must not be released from this tree.
$embeddedId=$runtime.Groups[1].Value
$sourcePath=Join-Path $PSScriptRoot 'src\EmbeddedRuntime.cs'
if(!(Test-Path -LiteralPath $sourcePath -PathType Leaf)){throw 'Cannot verify the executable: src\EmbeddedRuntime.cs is missing.'}
$sourceId=[regex]::Match([IO.File]::ReadAllText($sourcePath),'RuntimeId\s*=\s*"(\d+)"').Groups[1].Value
if($embeddedId -ne $sourceId){throw "Executable embeds runtime$embeddedId but src\EmbeddedRuntime.cs is at RuntimeId $sourceId. Rebuild before packaging."}
$shippedPath=Join-Path $PSScriptRoot 'releases\shipped-runtime-ids.txt'
if(Test-Path -LiteralPath $shippedPath -PathType Leaf){
 foreach($line in Get-Content -LiteralPath $shippedPath){
  $entry=$line.Trim()
  if(!$entry -or $entry.StartsWith('#')){continue}
  if($entry -notmatch '^\d+$'){throw "releases\shipped-runtime-ids.txt: '$entry' is not a runtime ID. Each line must be a bare ID, blank, or a comment starting with #."}
  if($entry -eq $embeddedId){throw "Executable embeds runtime$embeddedId, which was already published (releases\shipped-runtime-ids.txt)."}
 }
}
# Each result line starts with PASS or FAIL. A line that starts with FAIL (also FAILED: or FAILURE:) refuses the
# executable; a PASS line that merely mentions the word (for example "failure") does not. At least one line starting
# with PASS is required, so an executable that exits 0 and writes nothing is refused too, as is a non-zero exit code.
$selfTest=[IO.Path]::GetTempFileName()
try {
 $check=Start-Process -FilePath $exePath -ArgumentList '--self-test',"`"$selfTest`"" -Wait -PassThru
 # The report is a string, empty when the file is empty or missing, so that case fails the PASS requirement below.
 $report=''
 if(Test-Path -LiteralPath $selfTest -PathType Leaf){$report=[string](Get-Content -LiteralPath $selfTest -Raw)}
 if($report -match 'DRY RUN'){throw 'Executable reports itself as a PawPass dry-run build and is not packaged.'}
 if($check.ExitCode -ne 0 -or $report -notmatch '(?m)^\s*PASS' -or $report -match '(?m)^\s*FAIL'){throw "Executable failed its self-test:`n$report"}
} finally {
 Remove-Item -LiteralPath $selfTest -ErrorAction SilentlyContinue
}
# No manifest: an embedded checksum can't verify anything a corrupted download wouldn't also corrupt.
if(Test-Path -LiteralPath $OutputDirectory) {
 foreach($entry in Get-ChildItem -LiteralPath $OutputDirectory -Force) {
  if($entry.PSIsContainer -or $entry.Name -ne 'autocat.exe'){throw "Release output is not empty. Use a fresh output directory: $OutputDirectory"}
 }
} else {
 New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}
$released=Join-Path $OutputDirectory 'autocat.exe'
Copy-Item -LiteralPath $exePath -Destination $released -Force
Write-Output "Released: $released (runtime$($runtime.Groups[1].Value))"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $released -Algorithm SHA256).Hash)"
