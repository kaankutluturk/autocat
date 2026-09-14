param([string]$OutputDirectory=(Join-Path $PSScriptRoot '../../../work/diagnostic-tests'))
$ErrorActionPreference='Stop'
$run=Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $run | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /optimize+ /platform:x64 "/out:$run\LoggerTests.exe" (Join-Path $PSScriptRoot 'LoggerTests.cs') (Join-Path $PSScriptRoot '../../shared/Diagnostics.cs')
if($LASTEXITCODE -ne 0){throw 'Logger test compilation failed'}
& "$run\LoggerTests.exe" "$run\evidence" | Tee-Object -FilePath "$run\results.txt"
if($LASTEXITCODE -ne 0){throw 'Logger tests failed'}
"Evidence: $run"
