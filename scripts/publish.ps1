# Builds the packages users download, on Windows: one folder and one zip per system, with everything the program needs inside.
#
#   powershell -File scripts/publish.ps1 [win-x64] [linux-x64] ...      (default: win-x64)
#
# The same as scripts/publish.sh: the program (with .NET inside), the native libraries of the user interface beside it, and
# images/, sounds/, fonts/, language-specs/ and control2config.txt. Output: publish/<system>/ and publish/RemoteControl-<version>-<system>.zip.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Systems)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (-not $Systems -or $Systems.Count -eq 0) { $Systems = @('win-x64') }
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
foreach ($system in $Systems) {
    $out = Join-Path 'publish' $system
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet publish src/RemoteControl.App/RemoteControl.App.csproj -c Release -r $system --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=embedded `
        -p:EnableCompressionInSingleFile=true -o $out --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $system" }
    Get-ChildItem $out -Include *.pdb, *.xml -File | Remove-Item -Force
    $name = "RemoteControl-$version-$system"
    $staging = Join-Path 'publish' $name
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    Copy-Item -Recurse $out $staging
    $zip = Join-Path 'publish' "$name.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path $staging -DestinationPath $zip
    Remove-Item -Recurse -Force $staging
    Write-Host "$system`: $out and $zip"
}
