using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class BootstrapController : MonoBehaviour
{
    [SerializeField] private string _mainMenuSceneName = "MainMenu";

    private static BootstrapController _instance;
    private bool _bootstrapped;

    private async void Awake()
    {
        // DDOL + 중복 제거
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        if (_bootstrapped) return;
        _bootstrapped = true;

        await BootstrapAsync();
    }

    private async Task BootstrapAsync()
    {
        // 1) UGS Init
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            await UnityServices.InitializeAsync();
        }

        // 2) Auth (Anonymous)
        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        // 3) MainMenu 로드
        SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
    }
}
