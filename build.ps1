# Builds dist\CutePdfReader.exe with the C# compiler that ships inside Windows
# (.NET Framework 4.8). No Visual Studio, SDK or NuGet needed.
# -Out lets you build elsewhere while the reader in dist\ is running.
param([string]$Out)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $fw 'WPF'
$winmd = Join-Path $env:WINDIR 'System32\WinMetadata'
$csc = Join-Path $fw 'csc.exe'
$obj = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null
if (-not $Out) { $Out = "$dist\CutePdfReader.exe" }

$wpfRefs = @(
    "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Xml.dll"
)
$appRefs = $wpfRefs + @(
    "$fw\System.Core.dll", "$fw\System.Runtime.Serialization.dll", "$fw\Microsoft.VisualBasic.dll",
    "$fw\System.Net.Http.dll", "$fw\System.Windows.Forms.dll",
    # WinRT: the built-in Windows.Data.Pdf engine
    "$fw\System.Runtime.dll", "$fw\System.Runtime.WindowsRuntime.dll", "$fw\System.Threading.Tasks.dll",
    "$winmd\Windows.Foundation.winmd", "$winmd\Windows.Storage.winmd", "$winmd\Windows.Data.winmd", "$winmd\Windows.Graphics.winmd"
)
function RefArgs($list) { $list | ForEach-Object { "/r:$_" } }

function Invoke-Csc($argList) {
    & $csc /nologo /codepage:65001 @argList | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
}

Write-Host 'Icon...'
Invoke-Csc (@('/target:exe', "/out:$obj\IconGen.exe") + (RefArgs $wpfRefs) + @("$root\tools\IconGen.cs", "$root\src\CloudArt.cs"))
& "$obj\IconGen.exe" "$obj\app.ico" "$obj\icon-preview.png"
if ($LASTEXITCODE -ne 0) { throw 'IconGen failed' }

$resources = @(
    "/resource:$root\ui\Theme.xaml,CutePdf.Theme.xaml",
    "/resource:$root\ui\MainLayout.xaml,CutePdf.MainLayout.xaml"
)

# PDFium goes inside the exe gzipped (~7.5 MB -> ~3 MB); the app unpacks it on first run.
$pdfium = Join-Path $root 'third_party\pdfium'
if (-not (Test-Path "$pdfium\pdfium.dll")) { & "$root\tools\get-pdfium.ps1" }
$pdfiumVersion = ((Get-Content "$pdfium\VERSION.txt") -split '/')[-1].Trim()
$packed = "$obj\pdfium-$pdfiumVersion.dll.gz"
if (-not (Test-Path $packed) -or (Get-Item $packed).Length -lt 1MB) {
    Write-Host "Packing PDFium $pdfiumVersion..."
    $dllStream = [IO.File]::OpenRead("$pdfium\pdfium.dll")
    $gzStream = [IO.File]::Create($packed)
    $gzip = [IO.Compression.GZipStream]::new($gzStream, [IO.Compression.CompressionMode]::Compress)
    $dllStream.CopyTo($gzip)
    $gzip.Dispose(); $gzStream.Dispose(); $dllStream.Dispose()
}
$resources += "/resource:$packed,CutePdf.pdfium-$pdfiumVersion.dll.gz"
Write-Host 'App...'

$sources = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
Invoke-Csc (@('/target:winexe', '/optimize+', '/platform:anycpu', "/out:$Out",
    "/win32icon:$obj\app.ico", "/win32manifest:$root\src\app.manifest") + (RefArgs $appRefs) + $resources + $sources)

$exe = Get-Item $Out
Write-Host ("Done: {0} ({1:N0} KB)" -f $exe.FullName, ($exe.Length / 1KB))
