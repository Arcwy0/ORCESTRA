# Quest ASR/TTS Model Bundle

## Selected v1 Bundle

Use this bundle for the first Quest 3/3S implementation:

- ASR: `openai/whisper-tiny.en`, exported to Unity AI Inference / Sentis assets.
- TTS: Piper `en_US-lessac-medium` packaged in Unity
  `StreamingAssets`, with Android platform `TextToSpeech` as fallback.
- Audio input: push-to-talk, mono WAV, 16 kHz, 16-bit PCM.
- Runtime target: standalone Quest APK.

Full Unity object setup and validation flow:
`docs/unity_ai_robot_control_setup_guide.md`.

Rationale:

- The commands are short robotics instructions, so `tiny.en` is the lowest-risk
  latency/size tradeoff for initial English-only testing.
- Quest CPU/GPU budget should remain available for MR rendering, passthrough,
  robot preview, validation, and networking.
- Piper keeps speech synthesis on the Quest application side instead of the VLM
  server. Android TTS remains the fallback when the native Piper plugin is not
  installed.

If Russian or multilingual commands become required, replace ASR with Whisper
`tiny` multilingual and keep the same Unity-side interface.

## Unity Asset Layout

Place the exported ASR assets under:

```text
Assets/StreamingAssets/ASR/whisper-tiny-en/
```

Required files:

```text
Assets/StreamingAssets/ASR/whisper-tiny-en/LogMelSpectro.sentis
Assets/StreamingAssets/ASR/whisper-tiny-en/AudioEncoder_Tiny.sentis
Assets/StreamingAssets/ASR/whisper-tiny-en/AudioDecoder_Tiny.sentis
Assets/StreamingAssets/ASR/whisper-tiny-en/vocab.json
```

Place the Unity-side TTS assets under:

```text
Assets/StreamingAssets/TTS/piper-en_US-lessac-medium/en_US-lessac-medium.onnx
Assets/StreamingAssets/TTS/piper-en_US-lessac-medium/en_US-lessac-medium.onnx.json
```

Download them with:

```powershell
.\tools\download_unity_tts_model.ps1
```

Also assign the three `.sentis` files and `vocab.json` to
`RobotAiController` in the Unity Inspector:

- `Whisper Log Mel`
- `Whisper Encoder`
- `Whisper Decoder`
- `Whisper Vocab Json`

The current project includes the Piper TTS voice under `StreamingAssets`. ASR
weights are still not included; export/copy the Whisper Sentis bundle on the
server or a model-prep workstation.

## Implemented Now

- Microphone push-to-talk WAV recorder.
- PCM16 WAV decoder for on-device ASR input.
- WAV upload to the robot-AI server when using server-side ASR/VLM.
- Android TextToSpeech backend for Quest speech replies.
- Piper Unity-side model bundle metadata and native-plugin backend wrapper.
- Piper `en_US-lessac-medium` voice files under `StreamingAssets/TTS`.
- Speech backend selection in `RobotAiController`.
- Recommended ASR bundle manifest and readiness metadata.
- Inspector slots for Sentis Whisper log-mel, encoder, decoder, and vocab.
- Sentis Whisper greedy decoding loop adapted from the old Unity Sentis
  integration to Unity AI Inference.

## Validation Still Required

The Sentis Whisper decoder loop is implemented, but it has not been validated
against exported model assets in this repository because model weights are not
included. Validate the exact tensor names/shapes in Unity on Quest before using
it as the default input path.

Required validation before enabling local ASR execution:

1. Confirm encoder input shape for log-mel features.
2. Confirm decoder token input and KV-cache behavior.
3. Confirm output token logits shape and special token IDs.
4. Measure Quest 3/3S latency for 3 s, 5 s, and 10 s clips.
5. Confirm that `Functional.ArgMax(outputs[0], 2)` matches the decoder logits
   axis for the exported model.
6. Confirm memory usage during MR scene rendering.

## Server Fallback

For first full-system testing, keep `AiAsrBackendMode.ServerGateway` and send
recorded WAV bytes with the VLM request. The server can transcribe using Whisper
or another ASR service before prompt grounding.

This is the recommended path until on-device ASR latency and accuracy are
measured.

## Native Plugin Requirement

`PiperNativeTtsBackend` expects an Android class:

```text
com.orcestra.tts.PiperTtsPlugin
```

with a static factory:

```text
create(Activity activity, String modelAssetPath, String configAssetPath)
```

and instance methods:

```text
speak(String text)
shutdown()
```

The plugin should load the two Piper files from Unity `StreamingAssets` via
Android `AssetManager` or copy them to app-local storage, then run ONNX Runtime
plus Piper/eSpeak phonemization on-device. If the plugin is absent, the backend
falls back to Android TextToSpeech so speech still works during tests.

## Future Native Bundle Alternative

If Sentis Whisper is too slow or decoder implementation becomes brittle, use a
native Android plugin based on `sherpa-onnx`:

- ASR: streaming Zipformer/Paraformer or Whisper ONNX model.
- TTS: sherpa-onnx Kokoro/VITS or Piper-style voice.
- Plugin layout: `Assets/Plugins/Android/` for AAR/JNI libraries and
  `Assets/StreamingAssets/` for model files.

This likely gives better production control but requires Android native plugin
maintenance.
