function Get-GameFingerprint([string]$gameDir) {
    $names = @('Assembly-CSharp.dll', 'RimWorld.dll', 'UnityStubs.dll') +
        @(Get-ChildItem $gameDir -File | Where-Object Name -match '^(Waylith|Brutal)(\..+)?\.dll$' | ForEach-Object Name | Sort-Object) +
        @('System.Private.CoreLib.dll', 'coreclr.dll', 'clrjit.dll')
    $files = [ordered]@{}
    foreach ($n in $names) { $files[$n] = (Get-FileHash (Join-Path $gameDir $n) -Algorithm SHA256).Hash }
    $version = (Get-Content (Join-Path $gameDir 'Version.txt') -TotalCount 1).Trim()
    [pscustomobject]@{ Version = $version; Files = $files }
}
