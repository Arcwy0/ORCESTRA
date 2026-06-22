using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Task
{
    public string Description { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsCurrent { get; private set; }

    public Task(string description)
    {
        Description = description;
        IsCompleted = false;
        IsCurrent = false;
    }

    public void SetCompleted()
    {
        IsCompleted = true;
    }

    public void SetCurrent(bool current)
    {
        IsCurrent = current;
    }
}