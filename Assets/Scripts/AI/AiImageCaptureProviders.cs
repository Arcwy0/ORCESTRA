using System;
using System.Collections;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif
#if ORCESTRA_META_PCA
using Meta.XR;
#endif

namespace VRInteraction.AI
{
    public interface IAiImageCaptureProvider
    {
        IEnumerator Capture(Action<AiImageCapture> done);
    }

    public class UnityScreenshotCaptureProvider : IAiImageCaptureProvider
    {
        public IEnumerator Capture(Action<AiImageCapture> done)
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            done(new AiImageCapture
            {
                texture = tex,
                source = "unity_screenshot",
                width = tex != null ? tex.width : Screen.width,
                height = tex != null ? tex.height : Screen.height
            });
        }
    }

    public class QuestPassthroughCaptureProvider : IAiImageCaptureProvider
    {
        private const string SourceName = "quest_passthrough_camera";
        private const float FreshFrameTimeoutSeconds = 2f;
#if UNITY_ANDROID && !UNITY_EDITOR
        private const string HeadsetCameraPermission =
            "horizonos.permission.HEADSET_CAMERA";
#endif
#if ORCESTRA_META_PCA
        private static PassthroughCameraAccess _cameraAccess;
        private static DateTime _lastCapturedTimestamp;
#endif

        public IEnumerator Capture(Action<AiImageCapture> done)
        {
#if ORCESTRA_META_PCA && UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission))
            {
                Permission.RequestUserPermission(HeadsetCameraPermission);
                done(new AiImageCapture
                {
                    source = SourceName,
                    error = "Headset camera permission requested. Press SEND " +
                            "again after granting it."
                });
                yield break;
            }

            var access = EnsureCameraAccess();
            if (access == null)
            {
                done(new AiImageCapture
                {
                    source = SourceName,
                    error = "PassthroughCameraAccess component is unavailable."
                });
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + 5f;
            while (!access.IsPlaying && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!access.IsPlaying)
            {
                done(new AiImageCapture
                {
                    source = SourceName,
                    error = "PassthroughCameraAccess did not start streaming."
                });
                yield break;
            }

            var size = access.CurrentResolution;
            if (size.x <= 0 || size.y <= 0)
            {
                done(new AiImageCapture
                {
                    source = SourceName,
                    error = "PassthroughCameraAccess returned invalid resolution."
                });
                yield break;
            }

            DateTime previousTimestamp = _lastCapturedTimestamp;
            DateTime requestStartTimestamp = access.Timestamp;
            deadline = Time.realtimeSinceStartup + FreshFrameTimeoutSeconds;
            while (!AiPassthroughFrameGate.IsFreshForRequest(
                       access.Timestamp, previousTimestamp,
                       requestStartTimestamp) &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!AiPassthroughFrameGate.IsFreshForRequest(
                    access.Timestamp, previousTimestamp,
                    requestStartTimestamp))
            {
                done(new AiImageCapture
                {
                    source = SourceName,
                    error = "PassthroughCameraAccess did not provide a fresh " +
                            "camera frame."
                });
                yield break;
            }

            yield return new WaitForEndOfFrame();
            DateTime capturedTimestamp = access.Timestamp;
            Pose pose = access.GetCameraPose();
            var colors = access.GetColors();
            int pixelCount = size.x * size.y;
            if (!colors.IsCreated || colors.Length < pixelCount)
            {
                done(new AiImageCapture
                {
                    source = SourceName,
                    error = "PassthroughCameraAccess returned invalid color data."
                });
                yield break;
            }

            var tex = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(colors.GetSubArray(0, pixelCount));
            tex.Apply(false, false);
            _lastCapturedTimestamp = capturedTimestamp;
            Debug.Log("[RobotAI] Quest passthrough frame timestamp=" +
                      capturedTimestamp.ToString("O"));
            done(new AiImageCapture
            {
                texture = tex,
                source = SourceName,
                width = size.x,
                height = size.y,
                hasCameraMatrices = true,
                worldFromCamera = Matrix4x4.TRS(
                    pose.position, pose.rotation, Vector3.one),
                projection = Matrix4x4.identity,
                rayProvider = new QuestPassthroughRayProvider(access, pose)
            });
#else
            yield return null;
            done(new AiImageCapture
            {
                source = SourceName,
                width = 0,
                height = 0,
                error = "Quest raw camera capture is not compiled in. Install " +
                        "Meta MRUK v81+ and add ORCESTRA_META_PCA to scripting " +
                        "define symbols. Falling back to Unity screenshot."
            });
#endif
        }

#if ORCESTRA_META_PCA
        private static PassthroughCameraAccess EnsureCameraAccess()
        {
            if (_cameraAccess == null)
                _cameraAccess = UnityEngine.Object.FindAnyObjectByType<
                    PassthroughCameraAccess>(FindObjectsInactive.Include);
            if (_cameraAccess == null)
            {
                var go = new GameObject("RobotAI_PassthroughCameraAccess");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _cameraAccess = go.AddComponent<PassthroughCameraAccess>();
                _cameraAccess.CameraPosition =
                    PassthroughCameraAccess.CameraPositionType.Left;
                _cameraAccess.RequestedResolution = new Vector2Int(1280, 960);
            }
            _cameraAccess.enabled = true;
            return _cameraAccess;
        }

        private sealed class QuestPassthroughRayProvider : IAiImageRayProvider
        {
            private readonly PassthroughCameraAccess _access;
            private readonly Pose _cameraPose;

            public QuestPassthroughRayProvider(
                PassthroughCameraAccess access, Pose cameraPose)
            {
                _access = access;
                _cameraPose = cameraPose;
            }

            public string RaySource => SourceName;

            public bool TryCreateRay(
                Vector2 topLeftPixel, int imageWidth, int imageHeight,
                out Ray ray, out string error)
            {
                ray = default;
                error = null;
                if (_access == null || !_access.IsPlaying)
                {
                    error = "PassthroughCameraAccess is not playing.";
                    return false;
                }

                Vector2 viewport = AiImageRayUtil.TopLeftPixelToViewport(
                    topLeftPixel, imageWidth, imageHeight);
                ray = _access.ViewportPointToRay(viewport, _cameraPose);
                if (ray.direction.sqrMagnitude < 1e-8f)
                {
                    error = "PassthroughCameraAccess returned an invalid ray.";
                    return false;
                }
                return true;
            }
        }
#endif
    }
}
