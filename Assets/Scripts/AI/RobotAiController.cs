using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using VRInteraction.Placement;
using VRInteraction.AI.Speech;
using VRInteraction.UI;
#if UNITY_AI_INFERENCE
using Unity.InferenceEngine;
#endif

namespace VRInteraction.AI
{
    public class RobotAiController : MonoBehaviour
    {
        [Header("Server")]
        public AiCommandClient commandClient;
        public bool useLocalMock = true;
        public string serverUrl = "http://127.0.0.1:8080/v1/robot/command";

        [Header("Input")]
        public AiImageSourceMode imageSourceMode = AiImageSourceMode.UnityScreenshot;
        [TextArea(2, 4)]
        public string debugCommand = "Move the gripper to the cup on the table.";

        [Header("Speech")]
        public bool enableSpeech = true;
        public AiAsrBackendMode asrBackendMode = AiAsrBackendMode.ServerGateway;
        public AiTtsBackendMode ttsBackendMode = AiTtsBackendMode.AndroidTextToSpeech;
        public int recordSampleRateHz = 16000;
        public int maxRecordSeconds = 10;
        public bool useEditorMockAsrFallback = true;
        public string editorMockTranscript = "test test test";
        public AiSpeechModelBundle speechModelBundle = new AiSpeechModelBundle();
        public AiTtsModelBundle ttsModelBundle = new AiTtsModelBundle();
#if UNITY_AI_INFERENCE
        public ModelAsset whisperLogMel;
        public ModelAsset whisperEncoder;
        public ModelAsset whisperDecoder;
        public TextAsset whisperVocabJson;
#endif

        private readonly AiGroundingService _grounding = new AiGroundingService();
        private readonly AiPlanValidator _validator = new AiPlanValidator();
        private AiPlanPreview _preview;
        private AiMotionExecutor _executor;
        private AiCommandResponse _pending;
        private string _sessionId;
        private MicrophoneWavRecorder _recorder;
        private IAiAsrBackend _asrBackend;
        private IAiTtsBackend _ttsBackend;
        private string _liveAsrStartError;
        private byte[] _pendingAudioWav;
        private float _pendingAudioSeconds;
        private bool _isLiveAsrRecording;
        private System.Diagnostics.Stopwatch _liveAsrTimer;

        private GameObject _panel;
        private InputField _input;
        private Text _status;
        private Text _sourceLabel;

        private void Awake()
        {
            _sessionId = Guid.NewGuid().ToString("N");
            if (commandClient == null)
                commandClient = gameObject.AddComponent<AiCommandClient>();
            commandClient.serverUrl = serverUrl;
            commandClient.useLocalMock = useLocalMock;

            _preview = GetComponent<AiPlanPreview>() ??
                       gameObject.AddComponent<AiPlanPreview>();
            _executor = GetComponent<AiMotionExecutor>() ??
                        gameObject.AddComponent<AiMotionExecutor>();

            _recorder = new MicrophoneWavRecorder
            {
                sampleRateHz = recordSampleRateHz,
                maxSeconds = maxRecordSeconds
            };
            _asrBackend = CreateAsrBackend();
            _ttsBackend = CreateTtsBackend();
        }

        private void OnDestroy()
        {
            if (_asrBackend is IDisposable disposableAsr)
                disposableAsr.Dispose();
            if (_ttsBackend != null) _ttsBackend.Shutdown();
        }

        private void Start()
        {
            UiKit.EnsureEventSystem();
            BuildUi();
            SetStatus("AI robot control ready.");
        }

        private void OnSend()
        {
            if (AppState.Mode != AppMode.Idle)
            {
                SetStatus("Finish placement or waypoint task first.");
                return;
            }

            debugCommand = _input != null ? _input.text : debugCommand;
            if (string.IsNullOrEmpty(debugCommand) ||
                debugCommand.Trim().Length == 0)
            {
                if (_pendingAudioWav == null || _pendingAudioWav.Length == 0)
                {
                    SetStatus("Enter a command first.");
                    return;
                }
                debugCommand = "";
            }

            StartCoroutine(SendFlow());
        }

        private IEnumerator SendFlow()
        {
            SetStatus("Capturing image...");
            IAiImageCaptureProvider provider = imageSourceMode ==
                AiImageSourceMode.QuestPassthroughCamera
                    ? (IAiImageCaptureProvider)new QuestPassthroughCaptureProvider()
                    : new UnityScreenshotCaptureProvider();

            AiImageCapture capture = null;
            yield return provider.Capture(c => capture = c);
            if (capture == null || capture.texture == null)
            {
                if (imageSourceMode == AiImageSourceMode.QuestPassthroughCamera)
                {
                    SetStatus("Passthrough unavailable; using screenshot.");
                    provider = new UnityScreenshotCaptureProvider();
                    yield return provider.Capture(c => capture = c);
                }
            }

            if (capture == null || capture.texture == null)
            {
                SetStatus("Image capture failed.");
                yield break;
            }

            var request = AiSceneSnapshotBuilder.Build(
                _sessionId, debugCommand, capture.source,
                capture.width, capture.height);
            if (_pendingAudioWav != null && _pendingAudioWav.Length > 0)
            {
                request.audio_format = "wav";
                request.audio_sample_rate_hz = recordSampleRateHz;
            }

            SetStatus("Sending AI request...");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            AiCommandResponse response = null;
            string error = null;
            commandClient.serverUrl = serverUrl;
            commandClient.useLocalMock = useLocalMock;
            yield return commandClient.Send(
                request, capture.texture, _pendingAudioWav,
                (r, e) => { response = r; error = e; });
            _pendingAudioWav = null;
            _pendingAudioSeconds = 0f;

            Destroy(capture.texture);

            if (!string.IsNullOrEmpty(error))
            {
                SetStatus("AI request failed: " + error);
                yield break;
            }
            timer.Stop();
            Debug.Log($"[RobotAI] AI response latency: {timer.ElapsedMilliseconds} ms");

            ApplyTranscriptFromDiagnostics(response);
            LogResponseArtifacts(response);

            if (response != null && response.error != null &&
                !string.IsNullOrEmpty(response.error.message))
            {
                MarkRejection(response, response.error.message);
                SetStatus("AI rejected request: " + response.error.message);
                yield break;
            }

            if (!_grounding.EnsureWorldWaypoints(response, Camera.main, out error))
            {
                MarkRejection(response, error);
                SetStatus("Grounding failed: " + error);
                yield break;
            }

            if (!_validator.Validate(response, out error))
            {
                MarkRejection(response, error);
                SetStatus("Plan rejected: " + error);
                yield break;
            }

            _pending = response;
            _preview.Show(response);
            Speak(response.spoken_reply);
            SetStatus(string.IsNullOrEmpty(response.spoken_reply)
                ? "Plan ready. Confirm or cancel."
                : response.spoken_reply);
        }

        private void OnConfirm()
        {
            if (_pending == null)
            {
                SetStatus("No pending AI plan.");
                return;
            }

            if (_executor.Execute(_pending, out string error))
            {
                _preview.Clear();
                _pending = null;
                SetStatus("Executing AI plan.");
            }
            else SetStatus("Execution failed: " + error);
        }

        private void OnCancel()
        {
            _pending = null;
            _preview.Clear();
            _executor.Stop();
            SetStatus("AI plan cancelled.");
        }

        private void ToggleSource()
        {
            imageSourceMode = imageSourceMode == AiImageSourceMode.UnityScreenshot
                ? AiImageSourceMode.QuestPassthroughCamera
                : AiImageSourceMode.UnityScreenshot;
            RefreshSourceLabel();
        }

        private void ToggleRecording()
        {
            if (!enableSpeech)
            {
                SetStatus("Speech is disabled.");
                return;
            }

            if (_isLiveAsrRecording)
            {
                StopLiveAsrRecording();
                return;
            }

            if (_recorder.IsRecording)
            {
                byte[] wav = _recorder.Stop(
                    out _pendingAudioSeconds, out string error);
                if (!string.IsNullOrEmpty(error))
                {
                    SetStatus("Recording failed: " + error);
                    return;
                }
                _pendingAudioWav = wav;
                SetStatus($"Recorded {_pendingAudioSeconds:0.0}s audio.");
                if (asrBackendMode == AiAsrBackendMode.OnDeviceWhisperSentis ||
                    asrBackendMode == AiAsrBackendMode.NativePlugin ||
                    asrBackendMode == AiAsrBackendMode.ServerGateway)
                    StartCoroutine(TranscribePendingAudio());
                return;
            }

            if (_asrBackend is IAiLiveAsrBackend liveAsr)
            {
                StartLiveAsrRecording(liveAsr);
                return;
            }

            _recorder.sampleRateHz = recordSampleRateHz;
            _recorder.maxSeconds = maxRecordSeconds;
            if (_recorder.Start(out string startError))
                SetStatus("Recording... press REC again to stop.");
            else
                SetStatus("Recording failed: " + startError);
        }

        private void StartLiveAsrRecording(IAiLiveAsrBackend liveAsr)
        {
            if (liveAsr.StartListening(out string error))
            {
                _pendingAudioWav = null;
                _pendingAudioSeconds = 0f;
                _liveAsrStartError = null;
                _isLiveAsrRecording = true;
                _liveAsrTimer = System.Diagnostics.Stopwatch.StartNew();
                SetStatus($"{liveAsr.BackendName} listening... press REC again to stop.");
            }
            else
            {
                if (TrySwitchToEditorMockAsr(error, out IAiLiveAsrBackend fallback))
                {
                    StartLiveAsrRecording(fallback);
                    return;
                }

                SetStatus("ASR failed: " + error);
            }
        }

        private bool TrySwitchToEditorMockAsr(string reason,
            out IAiLiveAsrBackend fallback)
        {
            fallback = null;
#if UNITY_EDITOR
            if (!useEditorMockAsrFallback)
                return false;
            _liveAsrStartError = reason;
            _asrBackend = new EditorMockAsrBackend(() => editorMockTranscript);
            fallback = (IAiLiveAsrBackend)_asrBackend;
            SetStatus("Windows dictation unavailable; using Editor mock ASR.");
            RefreshSourceLabel();
            return true;
#else
            return false;
#endif
        }

        private void StopLiveAsrRecording()
        {
            if (_asrBackend is IAiLiveAsrBackend liveAsr)
                liveAsr.StopListening();
            _isLiveAsrRecording = false;
            _pendingAudioSeconds = _liveAsrTimer != null
                ? (float)_liveAsrTimer.Elapsed.TotalSeconds
                : 0f;
            _liveAsrTimer = null;
            SetStatus($"Recorded {_pendingAudioSeconds:0.0}s dictation.");
            StartCoroutine(TranscribePendingAudio());
        }

        private IEnumerator TranscribePendingAudio()
        {
            if (asrBackendMode == AiAsrBackendMode.ServerGateway)
            {
                yield return TranscribePendingAudioWithServer();
                yield break;
            }

            bool needsWav = !(_asrBackend is IAiLiveAsrBackend);
            if (needsWav &&
                (_pendingAudioWav == null || _pendingAudioWav.Length == 0))
                yield break;

            SetStatus("Running ASR...");
            AiAsrResult result = null;
            yield return _asrBackend.Transcribe(
                _pendingAudioWav, recordSampleRateHz, r => result = r);

            if (result == null || !string.IsNullOrEmpty(result.error))
            {
                SetStatus("ASR unavailable: " +
                          (result != null ? result.error : "no result"));
                yield break;
            }

            if (_input != null) _input.text = result.text;
            debugCommand = result.text;
            string note = string.IsNullOrEmpty(_liveAsrStartError)
                ? ""
                : " (mock fallback after: " + _liveAsrStartError + ")";
            _liveAsrStartError = null;
            SetStatus("ASR: " + result.text + note);
        }

        private IEnumerator TranscribePendingAudioWithServer()
        {
            if (_pendingAudioWav == null || _pendingAudioWav.Length == 0)
                yield break;

            SetStatus("Running server ASR...");
            AiTranscribeResponse response = null;
            string error = null;
            commandClient.serverUrl = serverUrl;
            commandClient.useLocalMock = false;
            yield return commandClient.TranscribeAudio(
                _pendingAudioWav,
                (r, e) => { response = r; error = e; });

            if (!string.IsNullOrEmpty(error))
            {
                SetStatus("Server ASR failed: " + error +
                          ". Audio will be sent with SEND.");
                yield break;
            }

            if (response != null && response.error != null &&
                !string.IsNullOrEmpty(response.error.message))
            {
                SetStatus("Server ASR unavailable: " +
                          response.error.message +
                          ". Audio will be sent with SEND.");
                yield break;
            }

            string text = response != null ? response.text : "";
            if (string.IsNullOrEmpty(text))
            {
                SetStatus("Server ASR returned empty text. Audio will be sent with SEND.");
                yield break;
            }

            if (_input != null) _input.text = text;
            debugCommand = text;
            _pendingAudioWav = null;
            _pendingAudioSeconds = 0f;
            SetStatus("ASR: " + text);
        }

        private void BuildUi()
        {
            var canvas = UiKit.WorldCanvas("AI_RobotControl", transform,
                new Vector3(0f, 1.18f, 1.45f), new Vector3(0f, 180f, 0f),
                new Vector2(900, 330), 0.0014f);
            _panel = canvas.gameObject;
            UiKit.Panel(canvas.transform, new Color(0.10f, 0.11f, 0.13f, 0.94f));

            var header = UiKit.Image("Header", canvas.transform,
                new Color(0.23f, 0.30f, 0.46f, 1f));
            UiKit.TopRow(UiKit.Rt(header), 0, 54, 0);
            var title = UiKit.Text("Title", header.transform,
                "  AI ROBOT COMMAND", 23, TextAnchor.MiddleLeft);
            UiKit.Stretch(title.rectTransform, 16, 0, 0, 0);

            BuildInput(canvas.transform);

            var send = UiKit.Button("Send", canvas.transform, "SEND", 22,
                new Color(0.20f, 0.45f, 0.28f, 1f), OnSend);
            UiKit.Box(UiKit.Rt(send), 24, 146, 180, 54);

            var confirm = UiKit.Button("Confirm", canvas.transform, "CONFIRM", 22,
                new Color(0.22f, 0.42f, 0.56f, 1f), OnConfirm);
            UiKit.Box(UiKit.Rt(confirm), 224, 146, 210, 54);

            var cancel = UiKit.Button("Cancel", canvas.transform, "CANCEL", 22,
                new Color(0.55f, 0.18f, 0.18f, 1f), OnCancel);
            UiKit.Box(UiKit.Rt(cancel), 454, 146, 190, 54);

            var source = UiKit.Button("Source", canvas.transform, "SOURCE", 22,
                new Color(0.32f, 0.34f, 0.42f, 1f), ToggleSource);
            UiKit.Box(UiKit.Rt(source), 664, 146, 210, 54);

            var rec = UiKit.Button("Record", canvas.transform, "REC", 22,
                new Color(0.42f, 0.34f, 0.20f, 1f), ToggleRecording);
            UiKit.Box(UiKit.Rt(rec), 664, 210, 210, 42);

            _sourceLabel = UiKit.Text("SourceLabel", canvas.transform, "",
                18, TextAnchor.MiddleLeft);
            UiKit.Box(UiKit.Rt(_sourceLabel), 24, 210, 620, 36);

            _status = UiKit.Text("Status", canvas.transform, "",
                18, TextAnchor.UpperLeft);
            UiKit.Box(UiKit.Rt(_status), 24, 250, 850, 64);
            RefreshSourceLabel();
        }

        private void BuildInput(Transform parent)
        {
            var bg = UiKit.Image("InputBg", parent,
                new Color(0.16f, 0.17f, 0.20f, 1f));
            UiKit.Box(UiKit.Rt(bg), 24, 76, 850, 54);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(bg.transform, false);
            var text = textGo.GetComponent<Text>();
            text.font = UiKit.Font;
            text.fontSize = 20;
            text.color = new Color(0.92f, 0.94f, 0.97f);
            text.alignment = TextAnchor.MiddleLeft;
            UiKit.Stretch(text.rectTransform, 14, 14, 0, 0);

            _input = bg.gameObject.AddComponent<InputField>();
            _input.textComponent = text;
            _input.text = debugCommand;
            _input.lineType = InputField.LineType.SingleLine;
        }

        private void RefreshSourceLabel()
        {
            if (_sourceLabel != null)
                _sourceLabel.text = "Image source: " + imageSourceMode +
                                    (useLocalMock ? "   |   mock server" : "") +
                                    $"   |   ASR: {asrBackendMode}" +
                                    EffectiveAsrLabel() +
                                    $"   |   TTS: {ttsBackendMode}";
        }

        private string EffectiveAsrLabel()
        {
            if (_asrBackend == null || _asrBackend.BackendName == null)
                return "";
            string mode = asrBackendMode.ToString();
            return _asrBackend.BackendName.Contains(mode)
                ? ""
                : $" ({_asrBackend.BackendName})";
        }

        private void SetStatus(string text)
        {
            if (_status != null) _status.text = text;
            Debug.Log("[RobotAI] " + text);
        }

        private void ApplyTranscriptFromDiagnostics(AiCommandResponse response)
        {
            if (response == null || response.diagnostics == null ||
                string.IsNullOrEmpty(response.diagnostics.transcript_text))
                return;

            string text = response.diagnostics.transcript_text;
            if (_input != null) _input.text = text;
            debugCommand = text;
            Debug.Log("[RobotAI] Server ASR transcript: " + text);
        }

        private static void LogResponseArtifacts(AiCommandResponse response)
        {
            if (response == null || response.diagnostics == null) return;

            if (!string.IsNullOrEmpty(response.diagnostics.saved_trace_path))
                Debug.Log("[RobotAI] Server trace: " +
                          response.diagnostics.saved_trace_path);
            if (!string.IsNullOrEmpty(response.diagnostics.saved_image_path))
                Debug.Log("[RobotAI] Server raw image: " +
                          response.diagnostics.saved_image_path);
            if (!string.IsNullOrEmpty(
                    response.diagnostics.saved_annotated_image_path))
                Debug.Log("[RobotAI] Server annotated image: " +
                          response.diagnostics.saved_annotated_image_path);
        }

        private static void MarkRejection(AiCommandResponse response, string reason)
        {
            if (response == null) return;
            if (response.diagnostics == null)
                response.diagnostics = new AiDiagnostics();
            response.diagnostics.rejection_reason = reason;
            Debug.LogWarning("[RobotAI] Rejection: " + reason);
        }

        private IAiAsrBackend CreateAsrBackend()
        {
            if (asrBackendMode == AiAsrBackendMode.EditorMock)
                return new EditorMockAsrBackend(() => editorMockTranscript);

            if (asrBackendMode == AiAsrBackendMode.WindowsDictation ||
                ShouldUseEditorDictationFallback())
                return new WindowsDictationAsrBackend();

            if (asrBackendMode == AiAsrBackendMode.OnDeviceWhisperSentis)
            {
#if UNITY_AI_INFERENCE
                return new SentisWhisperAsrBackend(
                    whisperLogMel, whisperEncoder, whisperDecoder,
                    whisperVocabJson, speechModelBundle);
#else
                return new UnavailableAsrBackend();
#endif
            }
            return new UnavailableAsrBackend();
        }

        private bool ShouldUseEditorDictationFallback()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return Application.isEditor &&
                   useLocalMock &&
                   asrBackendMode == AiAsrBackendMode.ServerGateway;
#else
            return false;
#endif
        }

        private IAiTtsBackend CreateTtsBackend()
        {
            if (ttsBackendMode == AiTtsBackendMode.NativePlugin ||
                ttsBackendMode == AiTtsBackendMode.PiperNativePlugin)
                return new PiperNativeTtsBackend(ttsModelBundle);
            if (ttsBackendMode == AiTtsBackendMode.AndroidTextToSpeech)
                return new AndroidTextToSpeechBackend();
            return new AndroidTextToSpeechBackend();
        }

        private void Speak(string text)
        {
            if (!enableSpeech || _ttsBackend == null) return;
            _ttsBackend.Speak(text);
        }
    }
}
