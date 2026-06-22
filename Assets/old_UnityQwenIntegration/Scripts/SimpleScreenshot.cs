using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;

public class SimpleScreenshot : MonoBehaviour
{
    //Saves a screenshot when a button is pressed

    public KeyCode screenShotButton;
    List<string> fileHistory = new List<string>();

    void Update()
    {
        string screenshotPath = string.Format("C:\\Arcwy\\UnityProjects\\VR\\VR_2024\\Assets", Application.persistentDataPath);
        Directory.CreateDirectory(screenshotPath);
        string path = string.Format("{0}/s_{1:yyyy_MM_dd_hh_mm_ss}.png", screenshotPath, DateTime.Now);
        if (Input.GetKeyDown(screenShotButton))
        {
            ScreenCapture.CaptureScreenshot(path);
            fileHistory.Add(path);
            Debug.Log("A screenshot was taken!");
        }
    }
}