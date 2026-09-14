param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../outputs'))
$ErrorActionPreference='Stop'
$binaryRoot=Join-Path $PSScriptRoot 'bin'
$exePath=Join-Path $binaryRoot 'autocat.exe'
if(!(Test-Path -LiteralPath $exePath -PathType Leaf)){throw 'Missing release binary: autocat.exe'}
# Resource names live in the UTF-8 metadata heap, not the UTF-16 heap C# string literals use.
$resourceName='AutoCat.Runtime.autocat.runtime.100.dll'
$exeBytes=[IO.File]::ReadAllBytes($exePath)
if(![Text.Encoding]::UTF8.GetString($exeBytes).Contains($resourceName)){throw 'Executable is missing the embedded runtime resource.'}
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
Write-Output "Released: $released"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $released -Algorithm SHA256).Hash)"
