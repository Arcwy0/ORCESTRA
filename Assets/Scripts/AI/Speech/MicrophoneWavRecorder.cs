using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace VRInteraction.AI.Speech
{
    public class MicrophoneWavRecorder
    {
        public int sampleRateHz = 16000;
        public int maxSeconds = 10;

        private string _device;
        private AudioClip _clip;
        private bool _recording;

        public bool IsRecording => _recording;

        public bool Start(out string error)
        {
            error = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
                error = "Microphone permission requested. Press REC again after granting it.";
                return false;
            }
#endif
            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                error = "No microphone device found.";
                return false;
            }

            _device = Microphone.devices[0];
            _clip = Microphone.Start(_device, false, maxSeconds, sampleRateHz);
            _recording = _clip != null;
            if (!_recording) error = "Microphone.Start failed.";
            return _recording;
        }

        public byte[] Stop(out float durationSeconds, out string error)
        {
            durationSeconds = 0f;
            error = null;
            if (!_recording || _clip == null)
            {
                error = "Recorder is not active.";
                return null;
            }

            int pos = Microphone.GetPosition(_device);
            Microphone.End(_device);
            _recording = false;

            if (pos <= 0)
            {
                error = "No microphone samples captured.";
                return null;
            }

            int channels = Mathf.Max(1, _clip.channels);
            var interleaved = new float[pos * channels];
            _clip.GetData(interleaved, 0);

            var mono = new float[pos];
            for (int i = 0; i < pos; i++)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                    sum += interleaved[i * channels + ch];
                mono[i] = sum / channels;
            }

            durationSeconds = pos / (float)_clip.frequency;
            return WavEncoder.EncodeMono16(mono, _clip.frequency);
        }
    }
}
