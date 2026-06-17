using System;
using System.Collections;
using UnityEngine;

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
        public IEnumerator Capture(Action<AiImageCapture> done)
        {
            yield return null;
            done(new AiImageCapture
            {
                source = "quest_passthrough",
                width = 0,
                height = 0,
                error = "Quest passthrough capture requires the Meta " +
                        "Passthrough Camera API device spike. Falling back " +
                        "to Unity screenshot."
            });
        }
    }
}
