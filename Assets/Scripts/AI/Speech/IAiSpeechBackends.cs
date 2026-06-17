using System;
using System.Collections;

namespace VRInteraction.AI.Speech
{
    public interface IAiAsrBackend
    {
        string BackendName { get; }
        IEnumerator Transcribe(byte[] wavBytes, int sampleRateHz,
            Action<AiAsrResult> done);
    }

    public interface IAiLiveAsrBackend : IAiAsrBackend
    {
        bool IsListening { get; }
        bool StartListening(out string error);
        void StopListening();
    }

    public interface IAiTtsBackend
    {
        string BackendName { get; }
        bool IsAvailable { get; }
        void Speak(string text);
        void Shutdown();
    }
}
