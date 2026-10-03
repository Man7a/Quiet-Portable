param([string]$Version='2.8.0-preview')
$ErrorActionPreference='Stop'
$portableProject=$PSScriptRoot
$build=Join-Path $portableProject 'build'
if(-not(Test-Path -LiteralPath (Join-Path $build 'QuietGPT.exe'))){throw 'Run build.ps1 first.'}
$artifacts=Join-Path $portableProject 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$stage=Join-Path $artifacts ('stage-'+[guid]::NewGuid().ToString('N'))
$app=Join-Path $stage 'Quiet Portable'
$source=Join-Path $stage 'Quiet Portable Source'
New-Item -ItemType Directory -Path $app,$source -Force | Out-Null
# Curated app copy: generated profiles, runtime downloads and tests never enter staging.
Get-ChildItem -LiteralPath $build -File | Where-Object { $_.Extension -in '.exe','.dll','.json','.ico','.dat','.txt' } | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $app}
foreach($folder in 'Avatar','Voice','runtimes'){
    $candidate=Join-Path $build $folder
    if(Test-Path -LiteralPath $candidate){Copy-Item -LiteralPath $candidate -Destination $app -Recurse}
}
foreach($document in 'README.md','THIRD-PARTY-NOTICES.md','VALIDATION.md','RELEASE_NOTES.md','Start Quiet.cmd','Set up voice.cmd'){
    Copy-Item -LiteralPath (Join-Path $portableProject $document) -Destination $app
}
$licenses=Join-Path $app 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$runtimeConfig=Get-Content -LiteralPath (Join-Path $build 'QuietGPT.runtimeconfig.json') -Raw | ConvertFrom-Json
$netVersion=($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App').version
Copy-Item -LiteralPath (Join-Path $portableProject ('.packages/microsoft.netcore.app.runtime.win-x64/'+$netVersion+'/LICENSE.TXT')) -Destination $app
Copy-Item -LiteralPath (Join-Path $portableProject ('.packages/microsoft.netcore.app.runtime.win-x64/'+$netVersion+'/THIRD-PARTY-NOTICES.TXT')) -Destination $app
foreach($package in 'microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64','microsoft.web.webview2','microsoft.windows.sdk.net.ref'){
    $packageFolder=Join-Path $portableProject ('.packages/'+$package)
    $packageVersion=Get-ChildItem -LiteralPath $packageFolder -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if($packageVersion){
        $target=Join-Path $licenses ($package+'/'+$packageVersion.Name)
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath $packageVersion.FullName -File | Where-Object { $_.Name -match 'LICENSE|NOTICE|\.nuspec$' } | ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination $target}
    }
}
foreach($file in 'README.md','THIRD-PARTY-NOTICES.md','VALIDATION.md','RELEASE_NOTES.md','build.ps1','package.ps1','.gitignore','Start Quiet.cmd','Set up voice.cmd'){
    Copy-Item -LiteralPath (Join-Path $portableProject $file) -Destination $source
}
$sourceDir=Join-Path $source 'src'
New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $portableProject 'src') -File | Where-Object { $_.Extension -in '.cs','.xaml','.csproj' -or $_.Name -eq 'app.manifest' } | ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination $sourceDir}
$sourceAssets=Join-Path $sourceDir 'Assets'
New-Item -ItemType Directory -Path $sourceAssets -Force | Out-Null
foreach($folder in 'Avatar','CompanionEyes','Voice'){
    Copy-Item -LiteralPath (Join-Path $portableProject ('src/Assets/'+$folder)) -Destination $sourceAssets -Recurse
}
foreach($icon in 'Quiet.ico','Quiet.png','Quiet.svg'){
    Copy-Item -LiteralPath (Join-Path $portableProject ('src/Assets/'+$icon)) -Destination $sourceAssets
}
Copy-Item -LiteralPath (Join-Path $portableProject 'tests') -Destination $source -Recurse
Copy-Item -LiteralPath $licenses -Destination $source -Recurse
# Refuse unsafe packages even if a future build accidentally adds such a folder.
foreach($tree in $app,$source){
    $bad=Get-ChildItem -LiteralPath $tree -Directory -Recurse | Where-Object Name -In 'Data','VoiceRuntime','test-results','Backups','__pycache__','Profile','.git','bin','obj'
    if($bad){throw 'Generated/private files reached staging. Packaging stopped.'}
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$appZip=Join-Path $artifacts ("Quiet-Portable-$Version-win-x64.zip")
$sourceZip=Join-Path $artifacts ("Quiet-Portable-$Version-source.zip")
foreach($archive in $appZip,$sourceZip){if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive}}
[IO.Compression.ZipFile]::CreateFromDirectory($app,$appZip,'Optimal',$true)
[IO.Compression.ZipFile]::CreateFromDirectory($source,$sourceZip,'Optimal',$true)
$hashes=@{}
foreach($archive in $appZip,$sourceZip){$hashes[[IO.Path]::GetFileName($archive)]=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.json') -Encoding UTF8
Write-Output $appZip
Write-Output $sourceZip
