# Piper TTS Voice

Place the Unity-side Piper voice files here:

```text
en_US-lessac-medium.onnx
en_US-lessac-medium.onnx.json
```

Use:

```powershell
.\tools\download_unity_tts_model.ps1
```

The voice files are packaged into the Quest build through `StreamingAssets`.
Runtime synthesis requires the Android Piper native plugin used by
`PiperNativeTtsBackend`.
