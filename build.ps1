param([string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\BongoCat', [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }) + @(Join-Path $PSScriptRoot 'shared\Diagnostics.cs')
$gameManaged = Join-Path $GameDirectory 'BongoCat_Data\Managed'
$referenceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('autocat-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $referenceDirectory | Out-Null
# A reference-only copy hides a trimmed compiler attribute. Game files are not edited.
$referenceBytes = [IO.File]::ReadAllBytes((Join-Path $gameManaged 'mscorlib.dll'))
$needle = [Text.Encoding]::ASCII.GetBytes('RuntimeCompatibilityAttribute')
$replacement = [Text.Encoding]::ASCII.GetBytes('RuntimeCompatibilitXAttribute')
for ($i=0; $i -le $referenceBytes.Length-$needle.Length; $i++) {
    if ($referenceBytes[$i] -ne $needle[0]) { continue }
    $matches = $true
    for ($j=1; $j -lt $needle.Length; $j++) { if ($referenceBytes[$i+$j] -ne $needle[$j]) { $matches=$false; break } }
    if ($matches) { [Array]::Copy($replacement,0,$referenceBytes,$i,$replacement.Length) }
}
$referencePath = Join-Path $referenceDirectory 'mscorlib.dll'
[IO.File]::WriteAllBytes($referencePath,$referenceBytes)
& $compilerPath /nologo /noconfig /define:AUTOCAT_GAME /nostdlib+ /target:library "/out:$OutputDirectory\autocat.runtime.100.dll" "/reference:$referencePath" "/reference:$gameManaged\System.dll" "/reference:$gameManaged\System.Core.dll" "/reference:$gameManaged\UnityEngine.CoreModule.dll" (Join-Path $PSScriptRoot 'bridge\Bridge.cs') (Join-Path $PSScriptRoot 'bridge\CosmeticPreview.cs') (Join-Path $PSScriptRoot 'bridge\NativeUnlock.cs') (Join-Path $PSScriptRoot 'bridge\InventoryHandoff.cs') (Join-Path $PSScriptRoot 'bridge\BufferedLog.cs') (Join-Path $PSScriptRoot 'bridge\WorkerDiagnostics.cs') (Join-Path $PSScriptRoot 'shared\Diagnostics.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runtime component compilation failed.' }
# Embedded so the download stays one file; Mono still needs a real file on disk, so src/EmbeddedRuntime.cs extracts it at launch.
& $compilerPath /nologo /target:winexe /win32icon:"$PSScriptRoot\assets\autocat.ico" /platform:x64 /optimize+ /warn:4 "/out:$OutputDirectory\autocat.exe" "/resource:$OutputDirectory\autocat.runtime.100.dll,AutoCat.Runtime.autocat.runtime.100.dll" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Remove-Item -LiteralPath $referencePath
Remove-Item -LiteralPath $referenceDirectory








