# Packaging checks. Needs a built bin/autocat.exe (run build.ps1 first); everything else is created under -OutputDirectory.
param([string]$OutputDirectory=(Join-Path $PSScriptRoot '../test-output/package-tests'),[string]$BinaryDirectory=(Join-Path $PSScriptRoot '../bin'),[string]$DryRunBinaryDirectory)
$ErrorActionPreference='Stop'
$source=Split-Path $PSScriptRoot -Parent
$run=Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))
$fixture=Join-Path $run 'fixture'
New-Item -ItemType Directory -Force "$fixture/bin","$fixture/src","$fixture/releases" | Out-Null
Copy-Item "$source/package.ps1" $fixture
Copy-Item "$source/src/EmbeddedRuntime.cs" "$fixture/src"
Copy-Item (Join-Path $BinaryDirectory 'autocat.exe') "$fixture/bin"
$output=Join-Path $run 'release'
$result=& "$fixture/package.ps1" -OutputDirectory $output
if((Get-FileHash "$output/autocat.exe").Hash -ne (Get-FileHash "$fixture/bin/autocat.exe").Hash){throw 'Released exe does not match source exe'}
if(($result -join "`n") -notmatch [regex]::Escape((Get-FileHash "$output/autocat.exe").Hash)){throw 'Reported hash does not match released file'}
'PASS: released exe is byte-identical to source and reports its own real hash'
& "$fixture/package.ps1" | Out-Null
if(!(Test-Path "$fixture/outputs/autocat.exe")){throw 'Default output directory is not the outputs folder inside the checkout'}
'PASS: without an output directory the release is written to a folder inside the checkout'
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
# Stand-in executables that carry a runtime resource of a chosen ID and fail their own self-test.
$stub=Join-Path $run 'stub'
New-Item -ItemType Directory -Force $stub | Out-Null
Set-Content (Join-Path $stub 'Stub.cs') 'static class Stub { static int Main(string[] a) { if (a.Length == 2 && a[0] == "--self-test") System.IO.File.WriteAllText(a[1], "FAIL: injected self-test failure"); return 1; } }'
function New-Stub([string]$id) {
 $resource=Join-Path $stub "autocat.runtime.$id.dll"
 Set-Content $resource 'placeholder'
 & (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:winexe "/out:$(Join-Path $fixture 'bin/autocat.exe')" "/resource:$resource,AutoCat.Runtime.autocat.runtime.$id.dll" (Join-Path $stub 'Stub.cs')
 if($LASTEXITCODE -ne 0){throw 'Stub compilation failed'}
}
function Assert-Refused([string]$name,[string]$pattern) {
 $rejected=$false
 try{& "$fixture/package.ps1" -OutputDirectory "$run/$name"}catch{if($_ -notmatch $pattern){throw};$rejected=$true}
 if(!$rejected -or (Test-Path "$run/$name")){throw "Not refused before release: $name"}
}
$currentId=[regex]::Match([IO.File]::ReadAllText("$fixture/src/EmbeddedRuntime.cs"),'RuntimeId\s*=\s*"(\d+)"').Groups[1].Value
New-Stub $currentId
Assert-Refused 'failed-self-test' 'failed its self-test'
'PASS: exe that fails its self-test is rejected before release'
# Only a result line that starts with FAIL is a failure; a PASS line may contain the word.
$failingStub=Get-Content (Join-Path $stub 'Stub.cs') -Raw
Set-Content (Join-Path $stub 'Stub.cs') 'static class Stub { static int Main(string[] a) { if (a.Length == 2 && a[0] == "--self-test") System.IO.File.WriteAllText(a[1], "PASS: a pipe failure is reported and recovered\nPASS: Failure of the update check stays quiet\n"); return 0; } }'
New-Stub $currentId
$wordOutput=Join-Path $run 'pass-with-word'
& "$fixture/package.ps1" -OutputDirectory $wordOutput | Out-Null
if(!(Test-Path "$wordOutput/autocat.exe")){throw 'A PASS line containing the word failure was treated as a failed self-test'}
'PASS: a PASS line that contains the word failure does not fail the self-test'
Set-Content (Join-Path $stub 'Stub.cs') 'static class Stub { static int Main(string[] a) { if (a.Length == 2 && a[0] == "--self-test") System.IO.File.WriteAllText(a[1], "PASS: first\nFAIL: second\n"); return 0; } }'
New-Stub $currentId
Assert-Refused 'fail-line-after-pass' 'failed its self-test'
'PASS: a FAIL result line is refused even when the exit code is 0 and PASS lines precede it'
foreach($case in @(@('failed-line','PASS: first\nFAILED: second\n'),@('failure-line','PASS: first\nFAILURE: second\n'),@('lower-case-fail','PASS: first\n  fail: second\n'),@('no-pass-line','nothing PASSED here\nstill no result line\n'))){
 Set-Content (Join-Path $stub 'Stub.cs') ('static class Stub { static int Main(string[] a) { if (a.Length == 2 && a[0] == "--self-test") System.IO.File.WriteAllText(a[1], "'+$case[1]+'"); return 0; } }')
 New-Stub $currentId
 Assert-Refused $case[0] 'failed its self-test'
}
'PASS: lines starting FAILED: or FAILURE: or in lower case are refused, and a report without a result line starting with PASS is refused, even with exit code 0'
Set-Content (Join-Path $stub 'Stub.cs') 'static class Stub { static int Main(string[] a) { return 0; } }'
New-Stub $currentId
Assert-Refused 'never-written' 'failed its self-test'
Set-Content (Join-Path $stub 'Stub.cs') 'static class Stub { static int Main(string[] a) { if (a.Length == 2 && a[0] == "--self-test") System.IO.File.WriteAllText(a[1], ""); return 0; } }'
New-Stub $currentId
Assert-Refused 'empty-report' 'failed its self-test'
Set-Content (Join-Path $stub 'Stub.cs') 'static class Stub { static int Main(string[] a) { if (a.Length == 2 && a[0] == "--self-test") System.IO.File.Delete(a[1]); return 0; } }'
New-Stub $currentId
Assert-Refused 'deleted-report' 'failed its self-test'
'PASS: an exe that exits 0 but writes an empty report, no report, or a deleted report is refused before release'
Set-Content (Join-Path $stub 'Stub.cs') $failingStub
New-Stub $currentId
New-Stub '999'
Assert-Refused 'stale-runtime' 'Rebuild before packaging'
'PASS: exe built from a different RuntimeId than the source is rejected before release'
# A dry-run build is recognized by a type name in the executable and, independently, by its self-test output.
$marked=[IO.File]::ReadAllBytes((Join-Path $BinaryDirectory 'autocat.exe'))+[Text.Encoding]::UTF8.GetBytes('PawPassDryRunBuild')
[IO.File]::WriteAllBytes((Join-Path $fixture 'bin/autocat.exe'),$marked)
Assert-Refused 'dry-run-marker' 'dry-run build and is not packaged'
Set-Content (Join-Path $stub 'Stub.cs') 'static class Stub { static int Main(string[] a) { if (a.Length == 2 && a[0] == "--self-test") System.IO.File.WriteAllText(a[1], "DRY RUN: stand-in\nPASS: stand-in"); return 0; } }'
New-Stub $currentId
Assert-Refused 'dry-run-self-test' 'reports itself as a PawPass dry-run build'
if($DryRunBinaryDirectory){
 Copy-Item (Join-Path $DryRunBinaryDirectory 'autocat.exe') (Join-Path $fixture 'bin/autocat.exe') -Force
 Assert-Refused 'dry-run-build' 'dry-run build and is not packaged'
 'PASS: an actual dry-run build is rejected before release'
}
'PASS: a PawPass dry-run exe is rejected before release, by its marker and by its self-test output'
Set-Content (Join-Path $stub 'Stub.cs') 'static class Stub { static int Main(string[] a) { if (a.Length == 2 && a[0] == "--self-test") System.IO.File.WriteAllText(a[1], "FAIL: injected self-test failure"); return 1; } }'
New-Stub $currentId
Set-Content "$fixture/releases/shipped-runtime-ids.txt" "# published`n100`n$currentId"
Assert-Refused 'shipped-runtime' 'already published'
'PASS: exe whose runtime ID was already published is rejected before release'
"Test files: $run"
