using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

#if UNITY_AI_INFERENCE
using Unity.InferenceEngine;
#endif

namespace VRInteraction.AI.Speech
{
    public class SentisWhisperAsrBackend : IAiAsrBackend, IDisposable
    {
        private const int MaxTokens = 100;
        private const int MaxSamples = 30 * 16000;
        private const int EndOfText = 50257;
        private const int StartOfTranscript = 50258;
        private const int English = 50259;
        private const int TranscribeToken = 50359;
        private const int NoTimestamps = 50363;

        public string BackendName => "sentis_whisper";

#if UNITY_AI_INFERENCE
        private readonly ModelAsset _logMelAsset;
        private readonly ModelAsset _encoderAsset;
        private readonly ModelAsset _decoderAsset;
        private readonly TextAsset _vocabJson;
        private readonly AiSpeechModelBundle _bundle;
        private readonly BackendType _backendType;

        private Worker _logMelWorker;
        private Worker _encoderWorker;
        private Worker _decoderWorker;
        private string[] _tokens;
        private char[] _whiteSpaceCharacters;

        public SentisWhisperAsrBackend(
            ModelAsset logMel, ModelAsset encoder, ModelAsset decoder,
            TextAsset vocabJson, AiSpeechModelBundle bundle)
        {
            _logMelAsset = logMel;
            _encoderAsset = encoder;
            _decoderAsset = decoder;
            _vocabJson = vocabJson;
            _bundle = bundle ?? new AiSpeechModelBundle();
            _backendType = BackendType.GPUCompute;
            SetupWhiteSpaceShifts();
        }
#else
        public SentisWhisperAsrBackend(UnityEngine.Object logMel,
            UnityEngine.Object encoder, UnityEngine.Object decoder,
            TextAsset vocabJson, AiSpeechModelBundle bundle) {}
#endif

       public IEnumerator Transcribe(byte[] wavBytes, int sampleRateHz,
            Action<AiAsrResult> done)
        {
#if UNITY_AI_INFERENCE
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (!ValidateAssets(out string assetError))
            {
                done(Fail(assetError));
                yield break;
            }

            if (!WavDecoder.TryDecodeMono16(wavBytes, out float[] samples,
                    out int wavRate, out string wavError))
            {
                done(Fail(wavError));
                yield break;
            }
            if (wavRate != AiSpeechModelBundle.RequiredSampleRateHz)
            {
                done(Fail("Whisper ASR requires 16 kHz WAV input. Got " +
                          wavRate + " Hz."));
                yield break;
            }
            if (samples.Length > MaxSamples)
            {
                done(Fail("Whisper ASR clip is longer than 30 seconds."));
                yield break;
            }

            try
            {
                EnsureInitialized();
            }
            catch (Exception exc)
            {
                done(Fail("Failed to initialize Whisper Sentis workers: " +
                          exc.Message));
                yield break;
            }

            string transcript = null;
            string error = null;
            yield return RunGreedyDecode(samples, (text, err) =>
            {
                transcript = text;
                error = err;
            });

            timer.Stop();
            if (!string.IsNullOrEmpty(error))
            {
                done(Fail(error));
                yield break;
            }

            done(new AiAsrResult
            {
                text = transcript,
                confidence = 0f,
                backend = BackendName,
                error = null
            });
#else
            yield return null;
            done(new AiAsrResult
            {
                backend = BackendName,
                error = "Unity AI Inference package is not resolved."
            });
#endif
        }

        public void Dispose()
        {
#if UNITY_AI_INFERENCE
            _decoderWorker?.Dispose();
            _encoderWorker?.Dispose();
            _logMelWorker?.Dispose();
            _decoderWorker = null;
            _encoderWorker = null;
            _logMelWorker = null;
#endif
        }

#if UNITY_AI_INFERENCE
        private bool ValidateAssets(out string error)
        {
            if (_logMelAsset == null || _encoderAsset == null ||
                _decoderAsset == null || _vocabJson == null)
            {
                error = "Assign Whisper log-mel, encoder, decoder, and " +
                        "vocab assets. Recommended bundle: " +
                        _bundle.Describe();
                return false;
            }

            error = null;
            return true;
        }

        private void EnsureInitialized()
        {
            if (_logMelWorker != null && _encoderWorker != null &&
                _decoderWorker != null && _tokens != null)
                return;

            _tokens = ParseVocab(_vocabJson.text);

            Model logMel = ModelLoader.Load(_logMelAsset);
            Model encoder = ModelLoader.Load(_encoderAsset);
            Model decoder = ModelLoader.Load(_decoderAsset);
            Model decoderWithArgMax = CompileArgMaxDecoder(decoder);

            _logMelWorker = new Worker(logMel, _backendType);
            _encoderWorker = new Worker(encoder, _backendType);
            _decoderWorker = new Worker(decoderWithArgMax, _backendType);
        }

        private static Model CompileArgMaxDecoder(Model decoder)
        {
            var graph = new FunctionalGraph();
            FunctionalTensor[] inputs = graph.AddInputs(decoder);
            FunctionalTensor[] outputs = Functional.Forward(decoder, inputs);
            FunctionalTensor tokenIds = Functional.ArgMax(outputs[0], 2);
            graph.AddOutput(tokenIds, "token_ids");
            return graph.Compile();
        }

        private IEnumerator RunGreedyDecode(float[] samples,
            Action<string, string> done)
        {
            float[] paddedSamples = new float[MaxSamples];
            Array.Copy(samples, paddedSamples, samples.Length);

            int[] outputTokens = new int[MaxTokens];
            outputTokens[0] = StartOfTranscript;
            outputTokens[1] = English;
            outputTokens[2] = TranscribeToken;
            outputTokens[3] = NoTimestamps;
            int currentToken = 3;
            var builder = new StringBuilder();

            Tensor<float> encodedAudio = null;
            try
            {
                using (var input = new Tensor<float>(
                           new TensorShape(1, MaxSamples), paddedSamples))
                {
                    _logMelWorker.Schedule(input);
                    Tensor<float> spectro =
                        _logMelWorker.PeekOutput() as Tensor<float>;
                    _encoderWorker.Schedule(spectro);
                    encodedAudio = _encoderWorker.PeekOutput() as Tensor<float>;
                }
            }
            catch (Exception exc)
            {
                done(null, "Whisper encoder failed: " + exc.Message);
                yield break;
            }

            while (currentToken < outputTokens.Length - 1)
            {
                int id;
                try
                {
                    using (var tokensSoFar = new Tensor<int>(
                               new TensorShape(1, outputTokens.Length),
                               outputTokens))
                    {
                        _decoderWorker.Schedule(tokensSoFar, encodedAudio);
                        Tensor<int> predictions =
                            _decoderWorker.PeekOutput() as Tensor<int>;
                        using (Tensor<int> cpu = predictions.ReadbackAndClone())
                        {
                            int[] predictedIds = cpu.DownloadToArray();
                            int index = Mathf.Clamp(currentToken, 0,
                                predictedIds.Length - 1);
                            id = predictedIds[index];
                        }
                    }
                }
                catch (Exception exc)
                {
                    done(null, "Whisper decoder failed: " + exc.Message);
                    yield break;
                }

                outputTokens[++currentToken] = id;
                if (id == EndOfText)
                    break;
                if (id >= 0 && id < _tokens.Length)
                    builder.Append(GetUnicodeText(_tokens[id]));

                yield return null;
            }

            done(builder.ToString().Trim(), null);
        }

        private static string[] ParseVocab(string json)
        {
            var entries = new Dictionary<int, string>();
            int idx = 0;
            while (idx < json.Length)
            {
                SkipToString(json, ref idx);
                if (idx >= json.Length) break;
                string token = ReadJsonString(json, ref idx);
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length || json[idx] != ':') break;
                idx++;
                SkipWhitespace(json, ref idx);
                int value = ReadInt(json, ref idx);
                entries[value] = token;
            }

            int size = 0;
            foreach (int key in entries.Keys)
                size = Math.Max(size, key + 1);
            var tokens = new string[size];
            foreach (var entry in entries)
                tokens[entry.Key] = entry.Value;
            return tokens;
        }

        private static void SkipToString(string json, ref int idx)
        {
            while (idx < json.Length && json[idx] != '"') idx++;
        }

        private static void SkipWhitespace(string json, ref int idx)
        {
            while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;
        }

        private static int ReadInt(string json, ref int idx)
        {
            int sign = 1;
            if (idx < json.Length && json[idx] == '-')
            {
                sign = -1;
                idx++;
            }
            int value = 0;
            while (idx < json.Length && char.IsDigit(json[idx]))
                value = value * 10 + json[idx++] - '0';
            return sign * value;
        }

        private static string ReadJsonString(string json, ref int idx)
        {
            var builder = new StringBuilder();
            if (json[idx] != '"') return "";
            idx++;
            while (idx < json.Length)
            {
                char ch = json[idx++];
                if (ch == '"') break;
                if (ch != '\\')
                {
                    builder.Append(ch);
                    continue;
                }
                if (idx >= json.Length) break;
                char escape = json[idx++];
                switch (escape)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escape);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        builder.Append(ReadUnicodeEscape(json, ref idx));
                        break;
                }
            }
            return builder.ToString();
        }

        private static char ReadUnicodeEscape(string json, ref int idx)
        {
            int value = 0;
            for (int i = 0; i < 4 && idx < json.Length; i++)
            {
                char ch = json[idx++];
                value <<= 4;
                if (ch >= '0' && ch <= '9') value += ch - '0';
                else if (ch >= 'a' && ch <= 'f') value += ch - 'a' + 10;
                else if (ch >= 'A' && ch <= 'F') value += ch - 'A' + 10;
            }
            return (char)value;
        }

        private string GetUnicodeText(string text)
        {
            string shifted = ShiftCharacterDown(text ?? "");
            byte[] bytes = new byte[shifted.Length];
            for (int i = 0; i < shifted.Length; i++)
                bytes[i] = (byte)(shifted[i] & 0xFF);
            return Encoding.UTF8.GetString(bytes);
        }

        private string ShiftCharacterDown(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (char letter in text)
            {
                if (letter <= 256)
                    builder.Append(letter);
                else
                    builder.Append(_whiteSpaceCharacters[(int)(letter - 256)]);
            }
            return builder.ToString();
        }

        private void SetupWhiteSpaceShifts()
        {
            _whiteSpaceCharacters = new char[256];
            int n = 0;
            for (int i = 0; i < 256; i++)
                if (IsWhiteSpace((char)i))
                    _whiteSpaceCharacters[n++] = (char)i;
        }

        private static bool IsWhiteSpace(char c)
        {
            return !(('!' <= c && c <= '~') ||
                     ('\u00A1' <= c && c <= '\u00AC') ||
                     ('\u00AE' <= c && c <= '\u00FF'));
        }

        private AiAsrResult Fail(string error)
        {
            return new AiAsrResult
            {
                backend = BackendName,
                error = error
            };
        }
#endif
    }
}
