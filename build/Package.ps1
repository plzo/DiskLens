#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$artifacts = Join-Path $root '.agent-artifacts'
$tools = Join-Path $artifacts 'tools'
$release = Join-Path $root 'release'
$reportDir = Join-Path $artifacts 'package-last'
$run = Join-Path $artifacts ('package-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $run 'payload'
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($d in @($artifacts, $tools, $release, $reportDir, $run)) { [void][IO.Directory]::CreateDirectory($d) }

function Check-Exit([string]$Stage) {
    if ($LASTEXITCODE -ne 0) { throw "$Stage failed (exit code $LASTEXITCODE)." }
}
function Get-PinnedZip([string]$Url, [string]$File, [string]$Algorithm, [string]$Hash) {
    if (!(Test-Path -LiteralPath $File -PathType Leaf) -or (Get-FileHash -LiteralPath $File -Algorithm $Algorithm).Hash -ne $Hash) {
        Write-Host "Downloading $([IO.Path]::GetFileName($File)) ..."
        & curl.exe -fLsS --retry 2 --connect-timeout 20 --output $File $Url
        Check-Exit 'Download'
    }
    if ((Get-FileHash -LiteralPath $File -Algorithm $Algorithm).Hash -ne $Hash) { throw "SHA mismatch: $File" }
}
function Get-BuilderSdk {
    $sdkVersion = (Get-Content -LiteralPath (Join-Path $root 'global.json') -Encoding UTF8 -Raw | ConvertFrom-Json).sdk.version
    $local = Join-Path $tools "dotnet-$sdkVersion"
    $localExe = Join-Path $local 'dotnet.exe'
    if (Test-Path -LiteralPath (Join-Path $local "sdk\$sdkVersion\dotnet.dll")) { return $localExe }
    $onPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath -and ((& $onPath.Source --list-sdks) -match ('^' + [regex]::Escape($sdkVersion) + ' '))) { return $onPath.Source }
    if ($sdkVersion -ne '10.0.404') { throw 'Update the pinned SDK archive hash in Package.ps1.' }
    $zip = Join-Path $tools "dotnet-$sdkVersion.zip"
    Get-PinnedZip "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip" $zip SHA512 `
        '330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee'
    [void][IO.Directory]::CreateDirectory($local)
    & tar.exe -xf $zip -C $local
    Check-Exit 'SDK extraction'
    return $localExe
}
function Get-InstallerCompiler {
    $exe = Join-Path $tools 'nsis-3.11\makensis.exe'
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
        $zip = Join-Path $tools 'nsis-3.11.zip'
        Get-PinnedZip 'https://sourceforge.net/projects/nsis/files/NSIS%203/3.11/nsis-3.11.zip/download' $zip SHA256 `
            'C7D27F780DDB6CFFB4730138CD1591E841F4B7EDB155856901CDF5F214394FA1'
        & tar.exe -xf $zip -C $tools
        Check-Exit 'NSIS extraction'
    }
    if ((& $exe /VERSION) -notmatch '^v3\.11$') { throw 'Unexpected NSIS version.' }
    return $exe
}
function Run-WindowsExe([string]$Exe, [string]$Arguments, [int]$ExpectedExit = 0, [int]$TimeoutSeconds = 600) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -WorkingDirectory $run -WindowStyle Hidden -PassThru
    if (!$p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill(); throw "Verification process timed out: $Exe" }
    if ($p.ExitCode -ne $ExpectedExit) { throw "Verification process failed (expected $ExpectedExit, exit $($p.ExitCode)): $Exe" }
}
function Test-App([string]$Directory, [string]$Label, [bool]$Capture = $false) {
    $report = Join-Path $run "$Label.json"
    $exe = Join-Path $Directory 'DiskLens.exe'
    $arg = '--self-test "' + $report + '"'
    if ($Capture) { $arg += ' --capture' }
    try { Run-WindowsExe $exe $arg }
    catch { if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report | Write-Host }; throw }
    $data = Get-Content -LiteralPath $report -Encoding UTF8 -Raw | ConvertFrom-Json
    if (!$data.ok -or $data.files -ne 9 -or $data.entries -ne 8) { throw "Self-test failed: $Label" }
    return $data
}
function Assert-ExactFiles([string]$Directory, [Collections.Generic.Dictionary[string,string]]$Inventory, [string]$Ignore = '') {
    $count = 0
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force) {
        $name = $file.FullName.Substring($Directory.Length + 1)
        if ($name -eq $Ignore) { continue }
        if (!$Inventory.ContainsKey($name) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $Inventory[$name]) {
            throw "File verification failed: $name in $Directory"
        }
        $count++
    }
    if ($count -ne $Inventory.Count) { throw "File count mismatch: $Directory" }
}
function Remove-VerifiedRun {
    $realRoot = (Resolve-Path -LiteralPath $root).ProviderPath
    $realBase = (Resolve-Path -LiteralPath $artifacts).ProviderPath
    $realRun = (Resolve-Path -LiteralPath $run).ProviderPath
    if ((Split-Path -Parent $realBase) -ne $realRoot -or (Split-Path -Parent $realRun) -ne $realBase -or
        (Split-Path -Leaf $realRun) -notmatch '^package-[0-9a-f]{32}$') { throw 'Refusing to remove a non-build directory.' }
    # Do not recurse through junctions, including in the verified scratch tree.
    foreach ($p in @($realRoot, $realBase, $realRun)) {
        if ([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) { throw 'Build scratch has a reparse point.' }
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($realRun)
    while ($pending.Count -gt 0) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            $attributes = [IO.File]::GetAttributes($entry)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Build scratch has a reparse point.' }
            if ($attributes -band [IO.FileAttributes]::Directory) { $pending.Push($entry) }
        }
    }
    Remove-Item -LiteralPath $realRun -Recurse -Force
}

$env:DOTNET_CLI_HOME = Join-Path $tools 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Push-Location -LiteralPath $root
try {
    $dotnet = Get-BuilderSdk
    $nsis = Get-InstallerCompiler
    $project = Join-Path $root 'src\DiskLens.csproj'
    $version = ([xml][IO.File]::ReadAllText($project)).Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a semantic version in DiskLens.csproj.' }
    Write-Host '[1/5] Running regression tests ...'
    $testOutput = Join-Path $run 'test-bin'
    & $dotnet build (Join-Path $root 'tests\DiskLens.Tests.csproj') -c Release --nologo -o $testOutput
    Check-Exit 'Regression build'
    & $dotnet (Join-Path $testOutput 'DiskLens.Tests.dll') (Join-Path $run 'test-fixtures') | Tee-Object -FilePath (Join-Path $run 'tests.log')
    Check-Exit 'Regression tests'

    Write-Host '[2/5] Publishing self-contained Windows x64 application ...'
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false --nologo -o $payload
    Check-Exit 'Publish'
    foreach ($required in @('DiskLens.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw "Missing runtime file: $required" }
    }
    $cfg = Get-Content -LiteralPath (Join-Path $payload 'DiskLens.runtimeconfig.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    $runtime = ($cfg.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    if (!$runtime) { throw 'Publish is not self-contained.' }
    $assets = Get-Content -LiteralPath (Join-Path $root 'src\obj\project.assets.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    $licenseDir = Join-Path $payload 'Licenses'
    [void][IO.Directory]::CreateDirectory($licenseDir)
    $licenses = [ordered]@{
        'Microsoft.NET-LICENSE.txt' = "microsoft.netcore.app.runtime.win-x64\$runtime\LICENSE.TXT"
        'Microsoft.NET-THIRD-PARTY-NOTICES.txt' = "microsoft.netcore.app.runtime.win-x64\$runtime\THIRD-PARTY-NOTICES.TXT"
        'WindowsDesktop-LICENSE.txt' = "microsoft.windowsdesktop.app.runtime.win-x64\$runtime\LICENSE"
    }
    foreach ($name in $licenses.Keys) {
        $origin = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $licenses[$name] } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (!$origin) { throw "Missing license from runtime pack: $name" }
        Copy-Item -LiteralPath $origin -Destination (Join-Path $licenseDir $name)
    }
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $nsis) 'COPYING') -Destination (Join-Path $licenseDir 'NSIS-COPYING.txt')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $payload 'README.md')

    $inventory = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $removal = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File -Force) {
        $name = $file.FullName.Substring($payload.Length + 1)
        if ($name -match '[$"\r\n]' -or $file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected payload path: $name" }
        $inventory.Add($name, (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)
        $removal.Add('Delete "$INSTDIR\' + $name + '"')
    }
    foreach ($dir in (Get-ChildItem -LiteralPath $payload -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending)) {
        $name = $dir.FullName.Substring($payload.Length + 1)
        if ($name -match '[$"\r\n]' -or $dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected payload directory: $name" }
        $removal.Add('RMDir "$INSTDIR\' + $name + '"')
    }
    $removeList = Join-Path $run 'uninstall-files.nsh'
    [IO.File]::WriteAllLines($removeList, $removal, $utf8)
    $portableName = 'DiskLens-Portable-win-x64.zip'
    $setupName = 'DiskLens-Setup-win-x64.exe'
    $portable = Join-Path $run $portableName
    $setup = Join-Path $run $setupName

    Write-Host '[3/5] Creating portable ZIP and per-user installer ...'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $portable, [IO.Compression.CompressionLevel]::Optimal, $false)
    & $nsis /V2 /INPUTCHARSET UTF8 "/DPAYLOAD=$payload" "/DOUTPUT=$setup" "/DVERSION=$version" `
        ("/DICON=" + (Join-Path $root 'src\DiskLens.ico')) "/DREMOVE_LIST=$removeList" (Join-Path $PSScriptRoot 'DiskLens.nsi')
    Check-Exit 'Installer compile'

    Write-Host '[4/5] Checking extracted and installed payloads without .NET on PATH ...'
    $originalPath = $env:Path
    $env:Path = "$env:SystemRoot\System32;$env:SystemRoot"
    try {
        $publishCheck = Test-App $payload 'published'
        $fresh = Join-Path $run 'fresh-zip'
        [IO.Compression.ZipFile]::ExtractToDirectory($portable, $fresh)
        Assert-ExactFiles $fresh $inventory
        $zipCheck = Test-App $fresh 'portable' $true
        $installed = Join-Path $run 'installed'
        Run-WindowsExe $setup ('/S /VERIFY /D=' + $installed)
        Assert-ExactFiles $installed $inventory 'Uninstall.exe'
        $installCheck = Test-App $installed 'installed'
        $uninstaller = Join-Path $installed 'Uninstall.exe'
        $lock = [IO.File]::Open((Join-Path $installed 'DiskLens.exe'), 'Open', 'Read', 'Read')
        try { Run-WindowsExe $uninstaller ('/S _?=' + $installed) 2 }
        finally { $lock.Dispose() }
        Assert-ExactFiles $installed $inventory 'Uninstall.exe'
        if (!(Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Locked-installation guard removed the uninstaller.' }
        [IO.File]::WriteAllText((Join-Path $installed 'preserve-this-test-file.txt'), 'not part of installer')
        Run-WindowsExe $uninstaller '/S'
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        while ((Test-Path -LiteralPath (Join-Path $installed 'Uninstall.exe')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
        $left = @(Get-ChildItem -LiteralPath $installed -Recurse -Force)
        if ($left.Count -ne 1 -or $left[0].Name -ne 'preserve-this-test-file.txt') { throw 'Uninstaller did not remove exactly its own files.' }
    } finally { $env:Path = $originalPath }

    Write-Host '[5/5] Publishing verified artifacts ...'
    $hashLines = foreach ($file in @($portable, $setup)) {
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($file)
    }
    foreach ($file in @($portable, $setup)) { Copy-Item -LiteralPath $file -Destination (Join-Path $release ([IO.Path]::GetFileName($file))) -Force }
    [IO.File]::WriteAllLines((Join-Path $release 'SHA256.txt'), [string[]]$hashLines, $utf8)
    $report = [ordered]@{ ok = $true; version = $version; runtime = $runtime; payloadFiles = $inventory.Count;
        published = $publishCheck; portable = $zipCheck; installed = $installCheck; uninstallRefusesLockedExe = $true; uninstallPreservesOtherFiles = $true; sha256 = $hashLines }
    [IO.File]::WriteAllText((Join-Path $reportDir 'report.json'), ($report | ConvertTo-Json -Depth 6), $utf8)
    Copy-Item -LiteralPath (Join-Path $run 'portable.png') -Destination (Join-Path $reportDir 'preview.png') -Force
    Copy-Item -LiteralPath (Join-Path $run 'portable.settings.png') -Destination (Join-Path $reportDir 'settings.png') -Force
    Copy-Item -LiteralPath (Join-Path $run 'portable.tasks.png') -Destination (Join-Path $reportDir 'tasks.png') -Force
    Copy-Item -LiteralPath (Join-Path $run 'portable.browser.png') -Destination (Join-Path $reportDir 'browser.png') -Force
    Copy-Item -LiteralPath (Join-Path $run 'tests.log') -Destination (Join-Path $reportDir 'tests.log') -Force
    try { Remove-VerifiedRun } catch { Write-Warning "Build scratch retained: $($_.Exception.Message)" }
    Write-Host "Done: $release"
} catch {
    Write-Warning "Build evidence kept at $run"
    throw
} finally { Pop-Location }
