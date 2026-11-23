[CmdletBinding()]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$out = Join-Path $root 'dist'
$obj = Join-Path $root 'obj'
[void][IO.Directory]::CreateDirectory($out)
[void][IO.Directory]::CreateDirectory($obj)
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','Accessibility.dll','System.Web.Extensions.dll','WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll','WPF\WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $fx $_) }
$common = @('/nologo','/noconfig','/nostdlib+','/platform:x64','/optimize+','/codepage:65001',('/reference:' + (Join-Path $fx 'mscorlib.dll'))) + $references
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$exe = Join-Path $out 'QuotaPeek.exe'
$iconFile = Join-Path $root 'assets\QuotaPeek.ico'
$compiler = Join-Path $fx 'csc.exe'
& $compiler @common /target:winexe /main:NhipPC.Program ("/win32manifest:" + (Join-Path $root 'app.manifest')) ("/win32icon:" + $iconFile) ("/out:" + $exe) @sources
if ($LASTEXITCODE) { throw 'Build failed.' }
Write-Output $exe
if ($Test) {
    $testExe = Join-Path $obj 'QuotaPeek.Tests.exe'
    $checks = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $compiler @common /target:exe /main:NhipPC.Checks ("/win32manifest:" + (Join-Path $root 'app.manifest')) ("/out:" + $testExe) @sources @checks
    if ($LASTEXITCODE) { throw 'Test build failed.' }
    & $testExe
    if ($LASTEXITCODE) { throw 'Tests failed.' }
}
