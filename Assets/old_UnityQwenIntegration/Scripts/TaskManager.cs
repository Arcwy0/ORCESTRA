using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;



public class TaskManager : MonoBehaviour
{
    public GameObject taskPanel;
    public TextMeshProUGUI taskTemplateText;
    private List<Task> tasks = new List<Task>();
    private FileInfo taskFile;
    private string taskName;
    [SerializeField] private string filePath = "TasksMedical.txt";
    private int currentTaskIndex = 0;

    public string FilePath { get => filePath; set => filePath = value; }

    void Start()
    {
        InitializeTasks();
        UpdateTaskListUI();
    }

    void InitializeTasks()
    {
        tasks.Add(new Task("Put orange into the bowl"));
        tasks.Add(new Task("Put apple into the bowl"));
        tasks.Add(new Task("Put banana into the bowl"));
        tasks.Add(new Task("Put peach into the bowl"));
        // Initialize the current task to be the first one
        tasks[currentTaskIndex].SetCurrent(true);
    }

    void InitializeTasksFromFile()
    {
        byte[] data = File.ReadAllBytes(FilePath);
        string taskData = Encoding.UTF8.GetString(data);
        string[] taskDescriptions = taskData.Split('\n');
        foreach (string taskDescription in taskDescriptions)
        {
            tasks.Add(new Task(taskDescription));
        }
        tasks[currentTaskIndex].SetCurrent(true);
    }

    void UpdateTaskListUI()
    {
        taskTemplateText.text = ""; // Clear existing text
        foreach (Task task in tasks)
        {
            string taskStatus = task.IsCompleted ? "<s>" + task.Description + "</s>" : task.Description;
            taskStatus = task.IsCurrent ? "<color=white>" + taskStatus + "</color>" : taskStatus;
            taskTemplateText.text += taskStatus + "\n";
        }
    }

    public bool CompleteTask(string taskDescription)
    {
        int taskIndex = tasks.FindIndex(t => t.Description == taskDescription);
        if (taskIndex == -1)
        {
            Debug.LogError("Task description not found.");
            return false; // Task description not found
        }

        if (taskIndex == currentTaskIndex)
        {
            tasks[taskIndex].SetCompleted();
            tasks[taskIndex].SetCurrent(false);
            if (currentTaskIndex + 1 < tasks.Count)
            {
                currentTaskIndex++;
                tasks[currentTaskIndex].SetCurrent(true);
            }
            UpdateTaskListUI();
            return true;
        }
        else
        {
            Debug.Log("Task completed out of order: " + taskDescription);
            // Reactivate the correct current task visually
            tasks[currentTaskIndex].SetCurrent(true);
            UpdateTaskListUI();
            return false;
        }
    }
}