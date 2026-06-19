using System;

namespace VRInteraction.AI.Speech
{
    public enum AiAsrBackendMode
    {
        ServerGateway,
        OnDeviceWhisperSentis,
        NativePlugin,
        WindowsDictation,
        EditorMock
    }

    public enum AiTtsBackendMode
    {
        AndroidTextToSpeech,
        UnityAudioClip,
        NativePlugin,
        PiperNativePlugin
    }

    [Serializable]
    public class AiAsrResult
    {
        public string text;
        public float confidence;
        public string backend;
        public string error;
    }

    [Serializable]
    public class AiSpeechMetrics
    {
        public float record_seconds;
        public int sample_rate_hz;
        public int wav_bytes;
        public string asr_backend;
        public string tts_backend;
    }
}
