using System;
using System.Collections;

namespace VRInteraction.AI.Speech
{
    public class UnavailableAsrBackend : IAiAsrBackend
    {
        public string BackendName => "unavailable";

        public IEnumerator Transcribe(byte[] wavBytes, int sampleRateHz,
            Action<AiAsrResult> done)
        {
            yield return null;
            done(new AiAsrResult
            {
                backend = BackendName,
                error = "No on-device ASR backend is configured."
            });
        }
    }
}
