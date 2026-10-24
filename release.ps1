# Publishes a new version to GitHub Releases; every installed reader offers to update itself.
#   1. Bump AssemblyVersion in src\AssemblyInfo.cs (e.g. 1.1.0.0 -> 1.2.0.0)
#   2. .\release.ps1 "What's new, in a sentence or two"
param([Parameter(Mandatory = $true)][string]$Notes)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = [regex]::Match((Get-Content "$root\src\Updater.cs" -Raw), 'Repository = "([^"]*)"').Groups[1].Value
if (-not $repo) { throw 'Set Updater.Repository in src\Updater.cs first, e.g. "yourname/CutePdfReader"' }
$version = [regex]::Match((Get-Content "$root\src\AssemblyInfo.cs" -Raw), 'AssemblyVersion\("(\d+\.\d+\.\d+)').Groups[1].Value
$tag = "v$version"

# Built outside dist\ so a running reader doesn't block it.
$out = "$root\obj\release\CutePdfReader.exe"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
& "$root\build.ps1" -Out $out

if (Get-Command gh -ErrorAction SilentlyContinue) {
    gh release create $tag $out --repo $repo --title "CutePdfReader $version" --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
    Write-Host "Published $tag"
} else {
    Write-Host "GitHub CLI (gh) is not installed - finish in the browser:"
    Write-Host "  1. The release page opens with tag $tag filled in"
    Write-Host "  2. Attach $out"
    Write-Host "  3. Press 'Publish release'"
    $url = "https://github.com/$repo/releases/new?tag=$tag&title=" + [uri]::EscapeDataString("CutePdfReader $version") + "&body=" + [uri]::EscapeDataString($Notes)
    Start-Process $url
    Start-Process explorer.exe "/select,`"$out`""
}
