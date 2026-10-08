param([string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\BongoCat', [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'), [switch]$PawPassDryRun)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$versionSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'src\AssemblyInfo.cs'))
$assemblyVersion = [regex]::Match($versionSource,'AssemblyVersion\("([\d.]+)"\)').Groups[1].Value
if (!$assemblyVersion -or $assemblyVersion -ne [regex]::Match($versionSource,'AssemblyFileVersion\("([\d.]+)"\)').Groups[1].Value) { throw 'AssemblyVersion and AssemblyFileVersion must be set and identical.' }
# The manifest identity and the runtime's file version are declared in their own files, so a mismatch fails the build.
$manifestPath = Join-Path $PSScriptRoot 'src\autocat.manifest'
$manifestVersion = [regex]::Match([IO.File]::ReadAllText($manifestPath),'<assemblyIdentity\s[^>]*version="([\d.]+)"').Groups[1].Value
if ($manifestVersion -ne $assemblyVersion) { throw "src\autocat.manifest declares version '$manifestVersion' but AssemblyVersion is $assemblyVersion." }
$runtimeInfoPath = Join-Path $PSScriptRoot 'bridge\RuntimeInfo.cs'
$runtimeFileVersion = [regex]::Match([IO.File]::ReadAllText($runtimeInfoPath),'AssemblyFileVersion\("([\d.]+)"\)').Groups[1].Value
if ($runtimeFileVersion -ne $assemblyVersion) { throw "bridge\RuntimeInfo.cs declares file version '$runtimeFileVersion' but AssemblyVersion is $assemblyVersion." }
# The runtime identity counter has a single source: src/EmbeddedRuntime.cs.
$runtimeId = [regex]::Match([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'src\EmbeddedRuntime.cs')),'RuntimeId\s*=\s*"(\d+)"').Groups[1].Value
if (!$runtimeId) { throw 'RuntimeId not found in src\EmbeddedRuntime.cs.' }
# A game process that already loaded a runtime ID keeps that code, so the ID follows the version and never repeats a published one.
$versionDigits = $assemblyVersion.Split('.')[0..2] -join ''
if ($runtimeId -ne $versionDigits) { throw "RuntimeId $runtimeId does not match version $assemblyVersion. Set RuntimeId in src\EmbeddedRuntime.cs to $versionDigits." }
# The list of published IDs is optional; a source tree without it still builds.
$shippedPath = Join-Path $PSScriptRoot 'releases\shipped-runtime-ids.txt'
if (Test-Path -LiteralPath $shippedPath -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $shippedPath) {
        $entry = $line.Trim()
        if (!$entry -or $entry.StartsWith('#')) { continue }
        if ($entry -notmatch '^\d+$') { throw "releases\shipped-runtime-ids.txt: '$entry' is not a runtime ID. Each line must be a bare ID, blank, or a comment starting with #." }
        if ($entry -eq $runtimeId) { throw "RuntimeId $runtimeId was already published (releases\shipped-runtime-ids.txt). Raise the version and RuntimeId before building a changed runtime." }
    }
}
$runtimeFile = "autocat.runtime.$runtimeId.dll"
Get-ChildItem -LiteralPath $OutputDirectory -Filter 'autocat.runtime.*.dll' | Remove-Item
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }) + @(Join-Path $PSScriptRoot 'shared\Diagnostics.cs')
$gameManaged = Join-Path $GameDirectory 'BongoCat_Data\Managed'
$referenceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('autocat-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $referenceDirectory | Out-Null
try {
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
# A dry-run build logs the PawPass claim it would make and contains no call that makes one. It is for a first
# observation session only: it names itself in the window title and self-test, and package.ps1 refuses it.
$dryRun = if ($PawPassDryRun) { 'AUTOCAT_PAWPASS_DRYRUN' } else { 'AUTOCAT_RELEASE' }
& $compilerPath /nologo /noconfig "/define:AUTOCAT_GAME;$dryRun" /nostdlib+ /target:library "/out:$OutputDirectory\$runtimeFile" "/reference:$referencePath" "/reference:$gameManaged\System.dll" "/reference:$gameManaged\System.Core.dll" "/reference:$gameManaged\UnityEngine.CoreModule.dll" (Join-Path $PSScriptRoot 'bridge\Bridge.cs') (Join-Path $PSScriptRoot 'bridge\CosmeticPreview.cs') (Join-Path $PSScriptRoot 'bridge\NativeUnlock.cs') (Join-Path $PSScriptRoot 'bridge\InventoryHandoff.cs') (Join-Path $PSScriptRoot 'bridge\PawPassClaim.cs') (Join-Path $PSScriptRoot 'bridge\BufferedLog.cs') (Join-Path $PSScriptRoot 'bridge\WorkerDiagnostics.cs') $runtimeInfoPath (Join-Path $PSScriptRoot 'shared\Diagnostics.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runtime component compilation failed.' }
# Embedded so the download stays one file; Mono still needs a real file on disk, so src/EmbeddedRuntime.cs extracts it at launch.
& $compilerPath /nologo "/define:$dryRun" /target:winexe /win32icon:"$PSScriptRoot\assets\autocat.ico" "/win32manifest:$manifestPath" /platform:x64 /optimize+ /warn:4 "/out:$OutputDirectory\autocat.exe" "/resource:$OutputDirectory\$runtimeFile,AutoCat.Runtime.$runtimeFile" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
} finally {
    # The patched reference copy of the game library must not stay in the temp folder, also when a compile fails.
    Remove-Item -LiteralPath $referenceDirectory -Recurse -Force -ErrorAction SilentlyContinue
}






