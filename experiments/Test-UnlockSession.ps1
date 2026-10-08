param([string]$OutputDirectory=(Join-Path $PSScriptRoot '../test-output/unlock-session-tests'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$fixtures=Join-Path $PSScriptRoot 'unlock-session'
& $compiler /nologo /target:library "/out:$OutputDirectory\UnityEngine.CoreModule.dll" (Join-Path $fixtures 'UnityFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Unity fixture compilation failed'}
& $compiler /nologo /target:library "/reference:$OutputDirectory\UnityEngine.CoreModule.dll" "/out:$OutputDirectory\UnityEngine.UI.dll" (Join-Path $fixtures 'UiFixture.cs')
if($LASTEXITCODE -ne 0){throw 'UI fixture compilation failed'}
& $compiler /nologo /target:library "/out:$OutputDirectory\Heathen.Steamworks.dll" (Join-Path $fixtures 'LobbyFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Lobby fixture compilation failed'}
& $compiler /nologo /warn:0 "/reference:$OutputDirectory\UnityEngine.CoreModule.dll" "/reference:$OutputDirectory\UnityEngine.UI.dll" "/reference:$OutputDirectory\Heathen.Steamworks.dll" "/out:$OutputDirectory\UnlockSessionTests.exe" (Join-Path $fixtures 'GameFixture.cs') (Join-Path $fixtures 'UnlockSessionTests.cs') (Join-Path $PSScriptRoot '../bridge/NativeUnlock.cs') (Join-Path $PSScriptRoot '../bridge/InventoryHandoff.cs')
if($LASTEXITCODE -ne 0){throw 'Unlock session fixture compilation failed'}
& "$OutputDirectory/UnlockSessionTests.exe" | Tee-Object -FilePath "$OutputDirectory/results.txt"
if($LASTEXITCODE -ne 0){throw 'Unlock session tests failed'}
