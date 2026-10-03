param([switch]$Test,[string]$TestPython='')
$ErrorActionPreference='Stop'
$portableProject=$PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $portableProject '.dotnet'
$env:NUGET_PACKAGES=Join-Path $portableProject '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
dotnet publish (Join-Path $portableProject 'src/QuietGPT.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $portableProject 'build') -p:PublishReadyToRun=false -p:NuGetAudit=false
if($LASTEXITCODE -ne 0){throw 'Portable build failed.'}
if($Test){
    if($TestPython){$env:QUIET_TEST_PYTHON=$TestPython}
    $process=Start-Process -FilePath (Join-Path $portableProject 'build/QuietGPT.exe') -ArgumentList '--smoke-test','--offline' -WindowStyle Hidden -PassThru -Wait
    if($process.ExitCode -ne 0){throw 'Native checks failed. See build/test-results/results.json.'}
}
Write-Output 'Portable build complete. Run package.ps1 to create clean archives.'
