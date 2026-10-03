param([switch]$CheckOnly,[string]$ExistingTurboFolder='')
$ErrorActionPreference='Stop'
$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$portableRoot=Split-Path -Parent $PSScriptRoot
$voiceRoot=Join-Path $portableRoot 'VoiceRuntime'
$setupLock=$null
try {
    if(-not [Environment]::Is64BitOperatingSystem){throw 'Quiet Turbo requires 64-bit Windows.'}
    New-Item -ItemType Directory -Path $voiceRoot -Force | Out-Null
    # File locking works across the native setup window and the optional batch launcher.
    $setupLock=[IO.File]::Open((Join-Path $voiceRoot 'setup.lock'),'OpenOrCreate','ReadWrite','None')
    $drive=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($voiceRoot))
    if($CheckOnly){Write-Output 'Setup prerequisite check passed.';exit 0}
    if(-not(Test-Path -LiteralPath (Join-Path $env:WINDIR 'System32/msvcp140.dll'))){throw 'The Microsoft Visual C++ v14 x64 Runtime is missing. Install it from https://aka.ms/vc14/vc_redist.x64.exe, then retry. The avatar can still run silently.'}
    if($drive.AvailableFreeSpace -lt 12GB){throw 'Allow at least 12 GB of free space for Python, CUDA packages, model weights and download cache.'}
    $nvidia=Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if(-not $nvidia){throw 'NVIDIA driver was not found. Install a current NVIDIA driver to use Turbo, or use Quiet silently.'}
    & $nvidia.Source --query-gpu=name --format=csv,noheader
    if($LASTEXITCODE -ne 0){throw 'The NVIDIA driver is not responding.'}
    Remove-Item -LiteralPath (Join-Path $voiceRoot 'ready.json') -Force -ErrorAction SilentlyContinue
    # No system Python, PATH changes, Windows installer, or administrator rights.
    $env:UV_CACHE_DIR=Join-Path $voiceRoot 'package-cache'
    $env:UV_PYTHON_INSTALL_DIR=Join-Path $voiceRoot 'python'
    $env:UV_PYTHON_BIN_DIR=Join-Path $voiceRoot 'bin'
    $env:UV_NO_MODIFY_PATH='1'
    $env:UV_PYTHON_DOWNLOADS='automatic'
    $env:QUIET_VOICE_RUNTIME=$voiceRoot
    $env:PYTHONIOENCODING='utf-8'
    if($ExistingTurboFolder){$env:QUIET_TURBO_SOURCE=(Resolve-Path -LiteralPath $ExistingTurboFolder).Path}else{Remove-Item Env:QUIET_TURBO_SOURCE -ErrorAction SilentlyContinue}
    $env:HF_HUB_DISABLE_TELEMETRY='1'
    $env:HF_HUB_DISABLE_XET='1'
    $env:HF_HUB_OFFLINE='0'
    $tools=Join-Path $voiceRoot 'tools'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    $uv=Join-Path $tools 'uv.exe'
    if(-not (Test-Path -LiteralPath $uv)) {
        Write-Output 'Downloading verified Python setup tool...'
        $archive=Join-Path $tools 'uv.zip'
        Invoke-WebRequest -Uri 'https://github.com/astral-sh/uv/releases/download/0.9.5/uv-x86_64-pc-windows-msvc.zip' -OutFile $archive
        if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '515dc53d7553f1357d0abc1f70acd921fbb9e30230b1d9a08737236daa6ee920'){throw 'Setup tool checksum did not match. Nothing was executed.'}
        Expand-Archive -LiteralPath $archive -DestinationPath $tools -Force
        Remove-Item -LiteralPath $archive
    }
    Write-Output 'Preparing portable Python 3.11.14...'
    & $uv python install 3.11.14 --no-bin
    if($LASTEXITCODE -ne 0){throw 'Python download failed. You can retry setup.'}
    $python=(Get-ChildItem -LiteralPath $env:UV_PYTHON_INSTALL_DIR -Directory | Where-Object Name -Like 'cpython-3.11.14-*' | Select-Object -First 1).FullName
    $python=Join-Path $python 'python.exe'
    if(-not (Test-Path -LiteralPath $python)){throw 'Portable Python could not be found.'}
    $packages=Join-Path $voiceRoot 'packages'
    Write-Output 'Installing Turbo and CUDA runtime packages. This may take several minutes...'
    # --target avoids absolute venv paths, so the extracted Quiet folder can be moved.
    & $uv pip install --quiet --python $python --target $packages -r (Join-Path $PSScriptRoot 'requirements-turbo.txt') -c (Join-Path $PSScriptRoot 'constraints-turbo.txt')
    if($LASTEXITCODE -ne 0){throw 'Voice dependencies could not be installed. Check the setup log, then retry.'}
    & $uv pip install --quiet --python $python --target $packages --no-deps 'https://github.com/resemble-ai/chatterbox/archive/5de7a54aa4e5e2baadb0182dde554908b48b85c2.zip'
    if($LASTEXITCODE -ne 0){throw 'Chatterbox could not be installed.'}
    $env:PYTHONPATH=$packages
    $env:PYTHONDONTWRITEBYTECODE='1'
    & $python -u (Join-Path $PSScriptRoot 'bootstrap-voice.py')
    if($LASTEXITCODE -ne 0){throw 'Voice validation failed. Quiet remains usable without speech. Retry setup after correcting the error.'}
    Write-Output 'Setup complete. The generated voice-test.wav is available in VoiceRuntime.'
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if($setupLock){$setupLock.Dispose()}
}
