param([string]$OutputDirectory=(Join-Path $PSScriptRoot '../test-output/pawpass-tests'),[string]$SourceRoot=(Join-Path $PSScriptRoot '..'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
$SourceRoot=(Resolve-Path -LiteralPath $SourceRoot).Path
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$fixtures=Join-Path $PSScriptRoot 'pawpass'
$sources=@('bridge\Bridge.cs','bridge\PawPassClaim.cs','bridge\WorkerDiagnostics.cs','bridge\InventoryHandoff.cs','shared\Diagnostics.cs') | ForEach-Object { Join-Path $SourceRoot $_ }
# The unchanged game model first, then one model per changed or missing member.
# The unchanged model again last, with the runtime compiled as a dry-run build.
foreach($variant in @('','NO_PAWPASS','NO_ROW_CLAIM','ROWS_RENAMED','UNCLAIMED_CHANGED','DRYRUN')){
 $folder=Join-Path $OutputDirectory $(if($variant){$variant}else{'game'})
 New-Item -ItemType Directory -Force $folder | Out-Null
 $define=if($variant -eq 'DRYRUN'){'/define:DRYRUN;AUTOCAT_PAWPASS_DRYRUN'}elseif($variant){"/define:VARIANT;$variant"}else{'/define:STANDARD'}
 & $compiler /nologo /target:library "/out:$folder\UnityEngine.CoreModule.dll" (Join-Path $fixtures 'UnityFixture.cs')
 if($LASTEXITCODE -ne 0){throw 'Unity fixture compilation failed'}
 & $compiler /nologo /target:library $define "/reference:$folder\UnityEngine.CoreModule.dll" "/out:$folder\Assembly-CSharp.dll" (Join-Path $fixtures 'GameFixture.cs')
 if($LASTEXITCODE -ne 0){throw "Game fixture compilation failed ($variant)"}
 & $compiler /nologo /warn:0 $define "/reference:$folder\UnityEngine.CoreModule.dll" "/reference:$folder\Assembly-CSharp.dll" "/out:$folder\PawPassTests.exe" (Join-Path $fixtures 'PawPassTests.cs') @sources
 if($LASTEXITCODE -ne 0){throw "PawPass fixture compilation failed ($variant)"}
 & "$folder\PawPassTests.exe" $SourceRoot (Join-Path $folder 'logs') | Tee-Object -FilePath "$folder\results.txt"
 if($LASTEXITCODE -ne 0){throw "PawPass tests failed ($(if($variant){$variant}else{'unchanged game'}))"}
}
