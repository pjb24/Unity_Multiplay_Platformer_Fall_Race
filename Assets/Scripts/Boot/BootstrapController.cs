using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class BootstrapController : MonoBehaviour
{
    [SerializeField] private string _mainMenuSceneName = "MainMenu";

    /// <summary>중복 부트스트랩을 방지하는 싱글톤 인스턴스입니다.</summary>
    private static BootstrapController _instance;
    /// <summary>UGS 초기화 루틴 1회 실행 보장 플래그입니다.</summary>
    private bool _bootstrapped;

    /// <summary>부트스트랩 싱글톤을 구성하고 초기화를 시작합니다.</summary>
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

    /// <summary>UGS 인증 이후 MainMenu 진입과 BGM 매니저 생성을 보장합니다.</summary>
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

        // 3) MainMenu 로드 전 BGM 매니저 생성 보장
        BgmManager.EnsureExists();
        BgmManager.Instance.PlayMainMenu();

        // 4) MainMenu 로드
        SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
    }
}
