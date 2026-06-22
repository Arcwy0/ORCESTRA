using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace VRInteraction.AI
{
    public class AiCommandClient : MonoBehaviour
    {
        [Tooltip("FastAPI gateway endpoint, e.g. http://SERVER:8080/v1/robot/command")]
        public string serverUrl = "http://127.0.0.1:8080/v1/robot/command";
        public bool useLocalMock = true;
        public bool useJsonTransport = true;
        public int timeoutSeconds = 180;

        public IEnumerator Send(
            AiCommandRequest request,
            Texture2D image,
            byte[] audioWav,
            Action<AiCommandResponse, string> done)
        {
            if (useLocalMock || string.IsNullOrEmpty(serverUrl) ||
                serverUrl.Trim().Length == 0)
            {
                done(MockResponse(request), null);
                yield break;
            }

            string json = JsonUtility.ToJson(request);
            if (useJsonTransport)
            {
                yield return SendJson(request, image, audioWav, done);
                yield break;
            }

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("request_json", json)
            };

            byte[] imagePng = image != null ? image.EncodeToPNG() : null;
            if (imagePng != null)
                form.Add(new MultipartFormFileSection(
                    "image", imagePng, "screenshot.png", "image/png"));
            if (audioWav != null && audioWav.Length > 0)
                form.Add(new MultipartFormFileSection(
                    "audio", audioWav, "command.wav", "audio/wav"));

            Debug.Log($"[RobotAI] POST {serverUrl} timeout={timeoutSeconds}s " +
                      $"json={json.Length} image={imagePng?.Length ?? 0}B " +
                      $"audio={audioWav?.Length ?? 0}B");

            using (var www = UnityWebRequest.Post(serverUrl, form))
            {
                www.timeout = timeoutSeconds;
#pragma warning disable 0618
                www.chunkedTransfer = false;
#pragma warning restore 0618
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    done(null, www.error + " http=" + www.responseCode +
                               ": " + www.downloadHandler.text);
                    yield break;
                }

                try
                {
                    var response = JsonUtility.FromJson<AiCommandResponse>(
                        www.downloadHandler.text);
                    done(response, null);
                }
                catch (Exception e)
                {
                    done(null, "Invalid AI response JSON: " + e.Message);
                }
            }
        }

        private IEnumerator SendJson(
            AiCommandRequest request,
            Texture2D image,
            byte[] audioWav,
            Action<AiCommandResponse, string> done)
        {
            byte[] imagePng = image != null ? image.EncodeToPNG() : null;
            string endpoint = serverUrl.EndsWith("/command_json")
                ? serverUrl
                : serverUrl.Replace("/v1/robot/command", "/v1/robot/command_json");
            var payload = new AiCommandJsonPayload
            {
                request = request,
                image_png_base64 = imagePng != null
                    ? Convert.ToBase64String(imagePng)
                    : "",
                audio_wav_base64 = audioWav != null && audioWav.Length > 0
                    ? Convert.ToBase64String(audioWav)
                    : ""
            };
            string payloadJson = JsonUtility.ToJson(payload);
            byte[] body = System.Text.Encoding.UTF8.GetBytes(payloadJson);

            Debug.Log($"[RobotAI] POST {endpoint} timeout={timeoutSeconds}s " +
                      $"transport=json json={payloadJson.Length} " +
                      $"image={imagePng?.Length ?? 0}B audio={audioWav?.Length ?? 0}B");

            using (var www = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                www.timeout = timeoutSeconds;
                www.uploadHandler = new UploadHandlerRaw(body);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    done(null, www.error + " http=" + www.responseCode +
                               ": " + www.downloadHandler.text);
                    yield break;
                }

                try
                {
                    var response = JsonUtility.FromJson<AiCommandResponse>(
                        www.downloadHandler.text);
                    done(response, null);
                }
                catch (Exception e)
                {
                    done(null, "Invalid AI response JSON: " + e.Message +
                               " raw=" + www.downloadHandler.text);
                }
            }
        }

        public IEnumerator Send(
            AiCommandRequest request,
            Texture2D image,
            Action<AiCommandResponse, string> done)
        {
            yield return Send(request, image, null, done);
        }

        public IEnumerator TranscribeAudio(
            byte[] audioWav,
            Action<AiTranscribeResponse, string> done)
        {
            if (audioWav == null || audioWav.Length == 0)
            {
                done(null, "No audio recorded.");
                yield break;
            }

            string endpoint = BuildTranscribeEndpoint(serverUrl);
            var payload = new AiAudioTranscribeJsonPayload
            {
                audio_wav_base64 = Convert.ToBase64String(audioWav)
            };
            string payloadJson = JsonUtility.ToJson(payload);
            byte[] body = System.Text.Encoding.UTF8.GetBytes(payloadJson);

            Debug.Log($"[RobotAI] POST {endpoint} timeout={timeoutSeconds}s " +
                      $"transport=json audio={audioWav.Length}B");

            using (var www = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                www.timeout = timeoutSeconds;
                www.uploadHandler = new UploadHandlerRaw(body);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    done(null, www.error + " http=" + www.responseCode +
                               ": " + www.downloadHandler.text);
                    yield break;
                }

                try
                {
                    var response = JsonUtility.FromJson<AiTranscribeResponse>(
                        www.downloadHandler.text);
                    done(response, null);
                }
                catch (Exception e)
                {
                    done(null, "Invalid ASR response JSON: " + e.Message +
                               " raw=" + www.downloadHandler.text);
                }
            }
        }

        private static AiCommandResponse MockResponse(AiCommandRequest request)
        {
            string robotId = request.robots != null && request.robots.Length > 0
                ? request.robots[0].id
                : "";
            string kind = request.robots != null && request.robots.Length > 0
                ? request.robots[0].kind
                : "manipulator";
            string planKind = kind == "mobile" ? "mobile_route" : "manipulator_reach";
            float[] world = MockWorldTarget(request);

            return new AiCommandResponse
            {
                spoken_reply = "Mock plan ready. Please confirm the highlighted motion.",
                intent = new AiIntent
                {
                    robot_id = robotId,
                    task_type = planKind,
                    target_ref = "mock target",
                    motion_primitive = "approach_standoff"
                },
                visual_grounding = new AiVisualGrounding
                {
                    label = "mock target",
                    confidence = 0.85f,
                    world_position_m = world,
                    world_confidence = 0.8f,
                    preferred_point_px = new[]
                    {
                        request.camera != null ? request.camera.width * 0.5f : Screen.width * 0.5f,
                        request.camera != null ? request.camera.height * 0.5f : Screen.height * 0.5f
                    }
                },
                visual_groundings = new[]
                {
                    new AiVisualGrounding
                    {
                        label = "mock target",
                        confidence = 0.85f,
                        world_position_m = world,
                        world_confidence = 0.8f,
                        preferred_point_px = new[]
                        {
                            request.camera != null ? request.camera.width * 0.5f : Screen.width * 0.5f,
                            request.camera != null ? request.camera.height * 0.5f : Screen.height * 0.5f
                        }
                    }
                },
                plan_ir = new AiPlanIr
                {
                    version = 1,
                    kind = planKind,
                    robot_id = robotId,
                    requires_confirmation = true,
                    contact_allowed = false,
                    min_clearance_m = 0.05f,
                    speed_scale = 0.25f,
                    waypoints = new AiWaypoint[0]
                },
                diagnostics = new AiDiagnostics
                {
                    request_id = request.session_id,
                    gateway_mode = "unity_local_mock"
                }
            };
        }

        private static float[] MockWorldTarget(AiCommandRequest request)
        {
            if (request.robots == null || request.robots.Length == 0)
                return new[] { 0f, 0f, 1f };

            var robot = request.robots[0];
            if (robot.kind == "mobile")
            {
                Vector3 root = AiModelUtil.ToVector3(robot.root_position_m);
                return AiModelUtil.Vec3(root + new Vector3(0.7f, 0f, 0f));
            }

            Vector3 center = AiModelUtil.ToVector3(robot.reach_center_m);
            float offset = Mathf.Clamp(robot.reach_radius_m * 0.35f, 0.12f, 0.35f);
            return AiModelUtil.Vec3(center + new Vector3(offset, 0f, 0f));
        }

        private static string BuildTranscribeEndpoint(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "";
            if (url.EndsWith("/v1/audio/transcribe_json"))
                return url;
            if (url.EndsWith("/v1/robot/command_json"))
                return url.Substring(0, url.Length - "/v1/robot/command_json".Length) +
                       "/v1/audio/transcribe_json";
            if (url.EndsWith("/v1/robot/command"))
                return url.Substring(0, url.Length - "/v1/robot/command".Length) +
                       "/v1/audio/transcribe_json";
            return url.TrimEnd('/') + "/v1/audio/transcribe_json";
        }

        [Serializable]
        private class AiCommandJsonPayload
        {
            public AiCommandRequest request;
            public string image_png_base64;
            public string audio_wav_base64;
        }

        [Serializable]
        private class AiAudioTranscribeJsonPayload
        {
            public string audio_wav_base64;
        }
    }
}
