# Downloads the pinned PDFium build (https://github.com/bblanchon/pdfium-binaries) into
# third_party\pdfium. build.ps1 calls this when the DLL is missing.
# To upgrade: change $Tag, delete third_party\pdfium, run this, then update $Sha256 with
# the hash it prints.

$ErrorActionPreference = 'Stop'
$Tag = 'chromium/8086'
$Sha256 = '1FD8AF952832DBB0EB16D9249F68FE09E5F5EBF7C3DD9F6066EA2720CC28487D'
$root = Split-Path $PSScriptRoot
$target = Join-Path $root 'third_party\pdfium'
$url = "https://github.com/bblanchon/pdfium-binaries/releases/download/$Tag/pdfium-win-x64.tgz"

# Unpack in a fresh temp folder; only the DLL and the licenses are kept.
$work = Join-Path ([IO.Path]::GetTempPath()) ("pdfium-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $archive = Join-Path $work 'pdfium.tgz'
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($Sha256 -and $hash -ne $Sha256) { throw "pdfium archive hash mismatch: $hash" }
    if (-not $Sha256) { Write-Host "SHA-256 of the archive (pin it in get-pdfium.ps1): $hash" }

    $unpacked = Join-Path $work 'x'
    New-Item -ItemType Directory $unpacked | Out-Null
    & tar.exe -xzf $archive -C $unpacked
    if ($LASTEXITCODE -ne 0) { throw 'tar failed' }

    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $unpacked 'bin\pdfium.dll') $target -Force
    Copy-Item (Join-Path $unpacked 'LICENSE') (Join-Path $target 'LICENSE.txt') -Force
    $licenses = Join-Path $unpacked 'licenses'
    if (Test-Path $licenses) { Copy-Item $licenses (Join-Path $target 'licenses') -Recurse -Force }
    Set-Content (Join-Path $target 'VERSION.txt') $Tag
    Write-Host "PDFium $Tag -> $target"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
