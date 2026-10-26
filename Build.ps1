$ErrorActionPreference = 'Stop'

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

$source = Join-Path $PSScriptRoot 'ResolutionSwitcher.cs'
$output = Join-Path $PSScriptRoot 'ResolutionSwitcher.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ "/out:$output" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll $source
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}
Write-Output "Built $output"
