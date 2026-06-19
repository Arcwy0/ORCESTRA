using System;
using System.IO;
using UnityEngine;

namespace VRInteraction.AI.Speech
{
    [Serializable]
    public class AiSpeechModelBundle
    {
        public const string RecommendedAsrBundleId = "whisper-tiny-en-sentis";
        public const string RecommendedAsrModel = "openai/whisper-tiny.en";
        public const string RecommendedTtsBundleId = "android-text-to-speech";
        public const int RequiredSampleRateHz = 16000;

        public string bundle_id = RecommendedAsrBundleId;
        public string source_model = RecommendedAsrModel;
        public string language = "en";
        public int sample_rate_hz = RequiredSampleRateHz;
        public string streaming_assets_root = "ASR/whisper-tiny-en";
        public string log_mel_model = "LogMelSpectro.sentis";
        public string encoder_model = "AudioEncoder_Tiny.sentis";
        public string decoder_model = "AudioDecoder_Tiny.sentis";
        public string vocab_json = "vocab.json";

        public string[] RequiredStreamingAssetRelativePaths()
        {
            return new[]
            {
                CombineRelative(streaming_assets_root, log_mel_model),
                CombineRelative(streaming_assets_root, encoder_model),
                CombineRelative(streaming_assets_root, decoder_model),
                CombineRelative(streaming_assets_root, vocab_json)
            };
        }

        public bool ValidateStreamingAssets(out string error)
        {
            foreach (string relativePath in RequiredStreamingAssetRelativePaths())
            {
                string absolutePath = Path.Combine(
                    Application.streamingAssetsPath, relativePath);
                if (!File.Exists(absolutePath))
                {
                    error = "Missing ASR model asset: StreamingAssets/" +
                            relativePath;
                    return false;
                }
            }

            error = null;
            return true;
        }

        public string Describe()
        {
            return $"{bundle_id} ({source_model}, {language}, " +
                   $"{sample_rate_hz} Hz)";
        }

        private static string CombineRelative(string left, string right)
        {
            return (left.TrimEnd('/', '\\') + "/" +
                    right.TrimStart('/', '\\')).Replace('\\', '/');
        }
    }

    [Serializable]
    public class AiTtsModelBundle
    {
        public const string RecommendedTtsBundleId = "piper-en_US-lessac-medium";
        public const string RecommendedTtsModel = "rhasspy/piper-voices/en_US-lessac-medium";

        public string bundle_id = RecommendedTtsBundleId;
        public string source_model = RecommendedTtsModel;
        public string language = "en-US";
        public int sample_rate_hz = 22050;
        public string streaming_assets_root = "TTS/piper-en_US-lessac-medium";
        public string model_file = "en_US-lessac-medium.onnx";
        public string config_file = "en_US-lessac-medium.onnx.json";

        public string ModelRelativePath =>
            CombineRelative(streaming_assets_root, model_file);

        public string ConfigRelativePath =>
            CombineRelative(streaming_assets_root, config_file);

        public string[] RequiredStreamingAssetRelativePaths()
        {
            return new[] { ModelRelativePath, ConfigRelativePath };
        }

        public bool ValidateStreamingAssets(out string error)
        {
            foreach (string relativePath in RequiredStreamingAssetRelativePaths())
            {
                string absolutePath = Path.Combine(
                    Application.streamingAssetsPath, relativePath);
                if (!File.Exists(absolutePath))
                {
                    error = "Missing TTS model asset: StreamingAssets/" +
                            relativePath;
                    return false;
                }
            }

            error = null;
            return true;
        }

        public string Describe()
        {
            return $"{bundle_id} ({source_model}, {language}, " +
                   $"{sample_rate_hz} Hz)";
        }

        private static string CombineRelative(string left, string right)
        {
            return (left.TrimEnd('/', '\\') + "/" +
                    right.TrimStart('/', '\\')).Replace('\\', '/');
        }
    }
}
