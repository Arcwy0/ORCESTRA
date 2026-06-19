using UnityEngine;

namespace VRInteraction.AI.Speech
{
    public class PiperNativeTtsBackend : IAiTtsBackend
    {
        private const string PluginClassName = "com.orcestra.tts.PiperTtsPlugin";

        private readonly AiTtsModelBundle _bundle;
        private readonly AndroidTextToSpeechBackend _fallback;
        private AndroidJavaObject _plugin;
        private bool _available;
        private string _lastError;

        public string BackendName => "piper_native_plugin";
        public bool IsAvailable => _available;
        public string LastError => _lastError;

        public PiperNativeTtsBackend(AiTtsModelBundle bundle)
        {
            _bundle = bundle ?? new AiTtsModelBundle();
            _fallback = new AndroidTextToSpeechBackend();
#if UNITY_ANDROID && !UNITY_EDITOR
            InitializeAndroid();
#else
            _available = false;
            _lastError = "Piper native TTS runs only on Android/Quest builds.";
#endif
        }

        public void Speak(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_available || _plugin == null)
            {
                Debug.LogWarning("[RobotAI] Piper TTS unavailable: " + _lastError);
                _fallback.Speak(text);
                return;
            }

            try
            {
                _plugin.Call("speak", text);
            }
            catch (System.Exception e)
            {
                _lastError = e.Message;
                Debug.LogWarning("[RobotAI] Piper TTS speak failed: " + e.Message);
            }
#else
            Debug.Log("[RobotAI:PiperTTS] " + text);
#endif
        }

        public void Shutdown()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_plugin != null)
            {
                try { _plugin.Call("shutdown"); }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[RobotAI] Piper TTS shutdown failed: " +
                                     e.Message);
                }
                _plugin.Dispose();
                _plugin = null;
            }
#endif
            _fallback.Shutdown();
            _available = false;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void InitializeAndroid()
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass(
                           "com.unity3d.player.UnityPlayer"))
                using (var pluginClass = new AndroidJavaClass(PluginClassName))
                {
                    var activity = unityPlayer.GetStatic<AndroidJavaObject>(
                        "currentActivity");
                    _plugin = pluginClass.CallStatic<AndroidJavaObject>(
                        "create", activity, _bundle.ModelRelativePath,
                        _bundle.ConfigRelativePath);
                }

                _available = _plugin != null;
                if (!_available)
                    _lastError = "Native plugin returned null.";
            }
            catch (System.Exception e)
            {
                _available = false;
                _lastError = e.Message;
                Debug.LogWarning("[RobotAI] Piper TTS init failed: " + e.Message);
            }
        }
#endif
    }
}
