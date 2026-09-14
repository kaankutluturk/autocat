param([string]$OutputDirectory=(Join-Path $PSScriptRoot '../../work/runtime100-tests'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /optimize+ "/out:$OutputDirectory/RuntimeLogicTests.exe" (Join-Path $PSScriptRoot 'RuntimeLogicTests.cs') (Join-Path $PSScriptRoot '../bridge/InventoryHandoff.cs')
if($LASTEXITCODE -ne 0){throw 'Runtime logic test compilation failed'}
& "$OutputDirectory/RuntimeLogicTests.exe" | Tee-Object -FilePath "$OutputDirectory/results.txt"
if($LASTEXITCODE -ne 0){throw 'Runtime logic tests failed'}
