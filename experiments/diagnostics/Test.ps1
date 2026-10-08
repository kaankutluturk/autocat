param([string]$OutputDirectory=(Join-Path $PSScriptRoot '../../test-output/diagnostic-tests'))
$ErrorActionPreference='Stop'
$run=Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))
# The longest file the tests create; past the classic Windows path limit the logger drops writes silently.
$longest=Join-Path $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($run) "logs\retention\diagnostic-$('0'*32).log.game.active"
if($longest.Length -ge 260){throw "Output directory path is $($longest.Length-259) characters too long for the logger tests: $OutputDirectory"}
New-Item -ItemType Directory -Force $run | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /optimize+ /platform:x64 "/out:$run\LoggerTests.exe" (Join-Path $PSScriptRoot 'LoggerTests.cs') (Join-Path $PSScriptRoot '../../shared/Diagnostics.cs')
if($LASTEXITCODE -ne 0){throw 'Logger test compilation failed'}
& "$run\LoggerTests.exe" "$run\logs" | Tee-Object -FilePath "$run\results.txt"
if($LASTEXITCODE -ne 0){throw 'Logger tests failed'}
"Test files: $run"
