param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\..").Path,
    [string]$Voice = "en_US-lessac-medium"
)

$ErrorActionPreference = "Stop"

$voiceDir = Join-Path $ProjectRoot "Assets\StreamingAssets\TTS\piper-$Voice"
New-Item -ItemType Directory -Force -Path $voiceDir | Out-Null

$baseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium"
$files = @(
    "$Voice.onnx",
    "$Voice.onnx.json"
)

foreach ($file in $files) {
    $target = Join-Path $voiceDir $file
    if (Test-Path $target -PathType Leaf) {
        Write-Host "Exists: $target"
        continue
    }

    $url = "$baseUrl/$file"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $target
    Write-Host "Wrote $target"
}

Write-Host "Unity TTS model files are ready in $voiceDir"
