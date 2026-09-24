param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../outputs'))
$ErrorActionPreference='Stop'
$binaryRoot=Join-Path $PSScriptRoot 'bin'
$exePath=Join-Path $binaryRoot 'autocat.exe'
if(!(Test-Path -LiteralPath $exePath -PathType Leaf)){throw 'Missing release binary: autocat.exe'}
# Resource names live in the UTF-8 metadata heap, not the UTF-16 heap C# string literals use. The exe's --self-test
# checks that the embedded name matches its own RuntimeId exactly.
$runtime=[regex]::Match([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($exePath)),'AutoCat\.Runtime\.autocat\.runtime\.(\d+)\.dll')
if(!$runtime.Success){throw 'Executable is missing the embedded runtime resource.'}
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
