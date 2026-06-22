using UnityEngine;

public class ScreenshotManager : MonoBehaviour
{
    public string screenshotPath = "Assets/Screenshots/screenshot.png"; // Set the path where the screenshot will be saved

    public void CaptureScreenshot()
    {
        ScreenCapture.CaptureScreenshot(screenshotPath);
    }
}