$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$assemblyInfo = Join-Path $root "Properties\AssemblyInfo.cs"
$buildInfo = Join-Path $root "BuildInfo.cs"
$hashFile = Join-Path $root ".build-source-hash"

$sourceFiles = @(
    (Join-Path $root "Program.cs"),
    (Join-Path $root "GENM_WEBSCRAPPER.csproj"),
    (Join-Path $root "App.config"),
    (Join-Path $root "packages.config"),
    $PSCommandPath
)
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $combined = [System.Text.StringBuilder]::new()
    foreach ($file in $sourceFiles) {
        [void]$combined.Append([System.IO.File]::ReadAllText($file))
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($combined.ToString())
    $currentHash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "")
} finally {
    $sha.Dispose()
}

$assemblyText = [System.IO.File]::ReadAllText($assemblyInfo)
$match = [regex]::Match($assemblyText, 'AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)')
if (-not $match.Success) { throw "No se encontró AssemblyVersion en AssemblyInfo.cs" }
$version = [int[]]@($match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value, $match.Groups[4].Value)
$previousHash = if (Test-Path $hashFile) { [System.IO.File]::ReadAllText($hashFile).Trim() } else { "" }
if ($currentHash -ne $previousHash) {
    $version[3]++
    $newVersion = $version -join "."
    $assemblyText = [regex]::Replace($assemblyText, 'AssemblyVersion\("[^"]+"\)', "AssemblyVersion(`"$newVersion`")")
    $assemblyText = [regex]::Replace($assemblyText, 'AssemblyFileVersion\("[^"]+"\)', "AssemblyFileVersion(`"$newVersion`")")
    [System.IO.File]::WriteAllText($assemblyInfo, $assemblyText, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($hashFile, $currentHash, [System.Text.UTF8Encoding]::new($false))
} else {
    $newVersion = $version -join "."
}

$compiledAt = [DateTime]::Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
$generated = @"
namespace GENM_WEBSCRAPPER
{
    internal static class BuildInfo
    {
        internal const string Version = "$newVersion";
        internal const string CompiledAt = "$compiledAt";
    }
}
"@
[System.IO.File]::WriteAllText($buildInfo, $generated, [System.Text.UTF8Encoding]::new($false))
