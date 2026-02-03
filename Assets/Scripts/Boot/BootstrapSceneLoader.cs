using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class BootstrapSceneLoader : MonoBehaviour
{
    [SerializeField] private string _lobbySceneName = "Lobby";

    private void Start()
    {
        if (SceneManager.GetActiveScene().name == _lobbySceneName)
            return;

        SceneManager.LoadScene(_lobbySceneName, LoadSceneMode.Single);
    }
}
