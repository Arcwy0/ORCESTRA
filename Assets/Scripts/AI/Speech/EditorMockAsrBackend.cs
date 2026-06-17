using System;
using System.Collections;
using UnityEngine;

namespace VRInteraction.AI.Speech
{
    public class EditorMockAsrBackend : IAiLiveAsrBackend
    {
        private readonly Func<string> _transcriptProvider;
        private float _startTime;

        public string BackendName => "editor_mock_asr";
        public bool IsListening { get; private set; }

        public EditorMockAsrBackend(Func<string> transcriptProvider)
        {
            _transcriptProvider = transcriptProvider;
        }

        public bool StartListening(out string error)
        {
            error = null;
            _startTime = Time.realtimeSinceStartup;
            IsListening = true;
            return true;
        }

        public void StopListening()
        {
            IsListening = false;
        }

        public IEnumerator Transcribe(byte[] wavBytes, int sampleRateHz,
            Action<AiAsrResult> done)
        {
            yield return null;
            string text = _transcriptProvider != null
                ? _transcriptProvider()
                : "";
            done(new AiAsrResult
            {
                text = string.IsNullOrWhiteSpace(text)
                    ? "test test test"
                    : text.Trim(),
                confidence = 1f,
                backend = BackendName,
                error = null
            });
        }

        public float ElapsedSeconds =>
            IsListening ? Time.realtimeSinceStartup - _startTime : 0f;
    }
}
