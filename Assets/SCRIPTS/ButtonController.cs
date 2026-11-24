using UnityEngine;
using UnityEngine.SceneManagement;

public class ButtonController : MonoBehaviour
{
    [SerializeField] private string mainMenu = "MainMenuVR";

    public void StartMainMenu()
    {
        Debug.Log($"Lade Szene: {mainMenu}");
        SceneManager.LoadScene(mainMenu);
    }
}
