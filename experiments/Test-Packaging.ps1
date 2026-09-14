param([string]$EvidenceDirectory=(Join-Path $PSScriptRoot '../../work/package-tests'))
$ErrorActionPreference='Stop'
$source=Split-Path $PSScriptRoot -Parent
$run=Join-Path $EvidenceDirectory ([Guid]::NewGuid().ToString('N'))
$fixture=Join-Path $run 'fixture'
New-Item -ItemType Directory -Force "$fixture/bin" | Out-Null
Copy-Item "$source/package.ps1" $fixture
Copy-Item "$source/bin/autocat.exe" "$fixture/bin"
$output=Join-Path $run 'release'
$result=& "$fixture/package.ps1" -OutputDirectory $output
if((Get-FileHash "$output/autocat.exe").Hash -ne (Get-FileHash "$fixture/bin/autocat.exe").Hash){throw 'Released exe does not match source exe'}
if(($result -join "`n") -notmatch [regex]::Escape((Get-FileHash "$output/autocat.exe").Hash)){throw 'Reported hash does not match released file'}
'PASS: released exe is byte-identical to source and reports its own real hash'
Set-Content "$output/private-settings.xml" 'must remain untouched'
$rejected=$false
try{& "$fixture/package.ps1" -OutputDirectory $output}catch{if($_ -notmatch 'not empty'){throw};$rejected=$true}
if(!$rejected -or (Get-Content "$output/private-settings.xml") -ne 'must remain untouched'){throw 'Output protection failed'}
'PASS: unexpected files in the output directory are rejected without deletion'
# Simulate a mis-built exe that never got the embedded runtime resource baked in.
Set-Content "$fixture/bin/autocat.exe" 'not a real executable' -NoNewline
$rejected=$false
try{& "$fixture/package.ps1" -OutputDirectory "$run/missing-resource"}catch{if($_ -notmatch 'missing the embedded runtime resource'){throw};$rejected=$true}
if(!$rejected -or (Test-Path "$run/missing-resource")){throw 'Missing embedded resource not refused before release'}
'PASS: exe missing the embedded runtime resource is rejected before release'
"Evidence: $run"
