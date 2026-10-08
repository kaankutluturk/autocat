param([string]$OutputDirectory=(Join-Path $PSScriptRoot '../../test-output/worker-diagnostics-tests'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
# A synthetic identity catches any release-specific hardcoded label.
$test=Join-Path $OutputDirectory 'autocat.runtime.999.exe'
& $compiler /nologo /warn:0 "/out:$test" (Join-Path $PSScriptRoot 'WorkerDiagnosticsTests.cs') (Join-Path $PSScriptRoot '../../bridge/WorkerDiagnostics.cs') (Join-Path $PSScriptRoot '../../shared/Diagnostics.cs')
if($LASTEXITCODE -ne 0){throw 'Worker diagnostics fixture compilation failed'}
& $test (Join-Path $OutputDirectory 'logs') | Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt')
if($LASTEXITCODE -ne 0){throw 'Worker diagnostics fixture failed'}
