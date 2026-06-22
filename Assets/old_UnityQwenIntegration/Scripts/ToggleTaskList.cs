using TMPro;
using UnityEngine;
using UnityEngine.UI;  // Ensure this using directive is included if using UI components

public class ToggleTaskList : MonoBehaviour
{
    public GameObject taskPanel;  // Reference to the task panel GameObject
    public Button toggleButton;   // Reference to the toggle button

    private bool isExpanded = false;  // Tracks the current state of the task panel

    void Start()
    {
        // Adds a listener to the toggle button that calls TogglePanel method when the button is clicked
        toggleButton.onClick.AddListener(TogglePanel);
        UpdateButtonLabel();  // Initialize the button label correctly
    }

    void TogglePanel()
    {
        isExpanded = !isExpanded;  // Toggle the expanded state
        taskPanel.SetActive(isExpanded);  // Set the active state of the panel based on the toggle
        UpdateButtonLabel();  // Update the button label to reflect the current state
    }

    void UpdateButtonLabel()
    {
        TextMeshProUGUI textComponent = toggleButton.GetComponentInChildren<TextMeshProUGUI>();
        if (textComponent != null)
        {
            textComponent.text = isExpanded ? "Hide Tasks" : "Show Tasks";
        }
        else
        {
            Debug.LogError("No TextMeshProUGUI component found on the toggle button");
        }
    }
}