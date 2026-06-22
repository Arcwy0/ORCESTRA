using UnityEngine;
using UnityEngine.UI;

public class RequestButton : MonoBehaviour
{
    public GameObject inputField; // Reference to the input field

    private ScreenshotManager screenshotManager;

    private void Start()
    {
        screenshotManager = FindObjectOfType<ScreenshotManager>();
    }

    public void Request()
    {
        // Capture screenshot
        screenshotManager.CaptureScreenshot();

        // Display input field
        inputField.SetActive(true);
    }
}