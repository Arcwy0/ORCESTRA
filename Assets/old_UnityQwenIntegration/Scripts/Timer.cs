using UnityEngine;
using System.IO;

public class Timer : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private float CurrentTime = 0f;
    private bool TimerIsActive = false;
    private string TimeLog = "Timer.txt";
    private string ParticipantName = "Participant";
    // Update is called once per frame
    void Update()
    {
        if (TimerIsActive)
        {
            CurrentTime += Time.deltaTime;
        }
    }

    public void StartTimer()
{
    TimerIsActive = true;
}
    public void StopTimer()
    {
        TimerIsActive = false;
    }
    public void ResetTimer()
    {
        CurrentTime = 0f;
    }
    public float GetTime()
    {
        return CurrentTime;
    }

    public void LogTime()
    {
        Debug.Log(CurrentTime);
        System.IO.File.AppendAllText(TimeLog,ParticipantName + ": " + CurrentTime.ToString() + "\n");
    }
}
