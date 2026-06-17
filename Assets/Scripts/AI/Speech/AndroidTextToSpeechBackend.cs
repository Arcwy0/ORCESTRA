using UnityEngine;

namespace VRInteraction.AI.Speech
{
    public class AndroidTextToSpeechBackend : IAiTtsBackend
    {
        private AndroidJavaObject _tts;
        private bool _available;

        public string BackendName => "android_text_to_speech";
        public bool IsAvailable => _available;

        public AndroidTextToSpeechBackend()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass(
                           "com.unity3d.player.UnityPlayer"))
                {
                    var activity = unityPlayer.GetStatic<AndroidJavaObject>(
                        "currentActivity");
                    _tts = new AndroidJavaObject(
                        "android.speech.tts.TextToSpeech",
                        activity,
                        new InitListener(this));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[RobotAI] Android TTS init failed: " + e.Message);
                _available = false;
            }
#else
            _available = false;
#endif
        }

        public void Speak(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_tts == null || !_available)
            {
                Debug.Log("[RobotAI] TTS unavailable: " + text);
                return;
            }

            using (var hashMap = new AndroidJavaObject("java.util.HashMap"))
            {
                _tts.Call<int>("speak", text, 0, hashMap);
            }
#else
            Debug.Log("[RobotAI:TTS] " + text);
#endif
        }

        public void Shutdown()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_tts != null)
            {
                _tts.Call("stop");
                _tts.Call("shutdown");
                _tts.Dispose();
                _tts = null;
            }
#endif
        }

        private void SetAvailable(bool available) => _available = available;

        private class InitListener : AndroidJavaProxy
        {
            private readonly AndroidTextToSpeechBackend _owner;

            public InitListener(AndroidTextToSpeechBackend owner)
                : base("android.speech.tts.TextToSpeech$OnInitListener")
            {
                _owner = owner;
            }

            public void onInit(int status)
            {
                _owner.SetAvailable(status == 0);
            }
        }
    }
}
