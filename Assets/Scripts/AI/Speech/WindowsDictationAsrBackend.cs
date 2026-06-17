using System;
using System.Collections;
using System.Text;
using UnityEngine;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using UnityEngine.Windows.Speech;
#endif

namespace VRInteraction.AI.Speech
{
    public class WindowsDictationAsrBackend : IAiLiveAsrBackend, IDisposable
    {
        public string BackendName => "windows_dictation";
        public bool IsListening { get; private set; }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private DictationRecognizer _recognizer;
        private readonly StringBuilder _finalText = new StringBuilder();
        private string _hypothesis = "";
        private string _error = "";
#endif

        public bool StartListening(out string error)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            error = null;
            _finalText.Length = 0;
            _hypothesis = "";
            _error = "";
            try
            {
                EnsureRecognizer();
                _recognizer.Start();
                IsListening = true;
                return true;
            }
            catch (Exception exc)
            {
                IsListening = false;
                error = exc.Message;
                return false;
            }
#else
            error = "Windows dictation is available only in Windows Editor or Windows standalone builds.";
            return false;
#endif
        }

        public void StopListening()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                if (_recognizer != null && IsListening)
                    _recognizer.Stop();
            }
            catch (Exception exc)
            {
                _error = exc.Message;
            }
            IsListening = false;
#endif
        }

        public IEnumerator Transcribe(byte[] wavBytes, int sampleRateHz,
            Action<AiAsrResult> done)
        {
            yield return new WaitForSeconds(0.25f);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string text = _finalText.ToString().Trim();
            if (string.IsNullOrEmpty(text))
                text = _hypothesis.Trim();

            done(new AiAsrResult
            {
                text = text,
                confidence = string.IsNullOrEmpty(text) ? 0f : 0.5f,
                backend = BackendName,
                error = string.IsNullOrEmpty(text)
                    ? "Windows dictation did not return text." +
                      (string.IsNullOrEmpty(_error) ? "" : " " + _error)
                    : null
            });
#else
            done(new AiAsrResult
            {
                backend = BackendName,
                error = "Windows dictation is unavailable on this platform."
            });
#endif
        }

        public void Dispose()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (_recognizer == null) return;
            if (IsListening) StopListening();
            _recognizer.Dispose();
            _recognizer = null;
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private void EnsureRecognizer()
        {
            if (_recognizer != null) return;

            _recognizer = new DictationRecognizer();
            _recognizer.DictationResult += OnDictationResult;
            _recognizer.DictationHypothesis += text => _hypothesis = text;
            _recognizer.DictationComplete += cause =>
            {
                IsListening = false;
                if (cause != DictationCompletionCause.Complete)
                    _error = "Dictation completed with cause: " + cause;
            };
            _recognizer.DictationError += (error, hresult) =>
            {
                IsListening = false;
                _error = error + " (0x" + hresult.ToString("X") + ")";
            };
        }

        private void OnDictationResult(string text, ConfidenceLevel confidence)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (_finalText.Length > 0) _finalText.Append(' ');
            _finalText.Append(text.Trim());
        }
#endif
    }
}
