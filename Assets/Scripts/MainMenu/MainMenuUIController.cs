using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class MainMenuUIController : MonoBehaviour
{
    // ============================
    // UI Refs (Inspector)
    // ============================
    [Header("UI Refs")]
    [SerializeField] private TMP_InputField _usernameInput;
    [SerializeField] private Button _quickSessionButton;
    [SerializeField] private Button _quitButton;
    [SerializeField] private TMP_Text _statusText;

    // ============================
    // Unity Messages
    // ============================
    /// <summary>메인 메뉴 UI 이벤트를 연결하고 MainMenu BGM 재생을 보장합니다.</summary>
    private void OnEnable()
    {
        BgmManager.EnsureExists();
        BgmManager.Instance.PlayMainMenu();

        if (_quickSessionButton == null || _quitButton == null || _statusText == null)
        {
            Debug.LogWarning("[MainMenu] UI fallback 발생: some references are missing.");
            return;
        }

        _quickSessionButton.onClick.AddListener(OnClickQuickSession);
        _quitButton.onClick.AddListener(OnClickQuit);
    }

    /// <summary>등록된 UI 버튼 이벤트를 해제합니다.</summary>
    private void OnDisable()
    {
        if (_quickSessionButton != null) _quickSessionButton.onClick.RemoveListener(OnClickQuickSession);
        if (_quitButton != null) _quitButton.onClick.RemoveListener(OnClickQuit);
    }

    // ============================
    // UI Callbacks
    // ============================
    /// <summary>QuickSession 시작 버튼 클릭을 처리합니다.</summary>
    private void OnClickQuickSession()
    {
        _ = RunQuickSessionAsync();
    }

    /// <summary>애플리케이션 종료 버튼 클릭을 처리합니다.</summary>
    private void OnClickQuit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ============================
    // Flow
    // ============================
    /// <summary>QuickSession 참가/생성 흐름을 실행합니다.</summary>
    private async Task RunQuickSessionAsync()
    {
        if (!TryGetContext(out var ctx)) return;

        string username = GetOrGenerateUsername();

        SetInteractable(false);
        SetStatus($"QuickSession 시작 중... ({username})");

        // 1) Client Quick Join 우선 시도
        SetStatus("로비 참가 시도 중...");
        var joinResult = await ctx.TryJoinAsClientThenEnterGameAsync(username);

        if (joinResult.ok)
        {
            SetStatus("Client 참가 성공. Game 로드 중...");
            return;
        }

        Debug.LogWarning($"[MainMenu] QuickSession fallback 발생: join attempt failed: {joinResult.failCode} / {joinResult.message}");
        SetStatus("참가 불가. Host 로비 생성 시도 중...");

        // 2) Join 불가면 Host로 Lobby 생성
        var hostResult = await ctx.TryStartAsHostThenEnterGameAsync(username);

        if (hostResult.ok)
        {
            SetStatus($"Host 생성 성공. JoinCode={hostResult.relayJoinCode} / Game 로드 중...");
            return;
        }

        Debug.LogWarning($"[MainMenu] QuickSession fallback 발생: host attempt failed: {hostResult.failCode} / {hostResult.message}");
        SetStatus($"QuickSession 실패: {hostResult.message}");
        SetInteractable(true);
    }

    // ============================
    // Validation / Helpers
    // ============================
    /// <summary>QuickSessionContext 존재 여부를 검사합니다.</summary>
    private bool TryGetContext(out QuickSessionContext ctx)
    {
        ctx = QuickSessionContext.Instance;
        if (ctx == null)
        {
            Debug.LogWarning("[MainMenu] UI fallback 발생: QuickSessionContext.Instance is null. Bootstrap DDOL 확인.");
            SetStatus("세션 컨텍스트가 없다. Bootstrap부터 시작해라.");
            return false;
        }
        return true;
    }

    /// <summary>입력값 또는 랜덤 규칙으로 사용자 이름을 결정합니다.</summary>
    private string GetOrGenerateUsername()
    {
        string raw = _usernameInput != null ? _usernameInput.text : null;
        raw = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

        if (string.IsNullOrEmpty(raw))
        {
            // 규칙: 비어있으면 생성 후 사용
            return $"User{Random.Range(1000, 9999)}";
        }
        return raw;
    }

    /// <summary>메뉴 버튼의 입력 가능 상태를 일괄 반영합니다.</summary>
    private void SetInteractable(bool on)
    {
        if (_quickSessionButton != null) _quickSessionButton.interactable = on;
        if (_quitButton != null) _quitButton.interactable = on;
    }

    /// <summary>상태 텍스트를 안전하게 갱신합니다.</summary>
    private void SetStatus(string msg)
    {
        if (_statusText != null) _statusText.text = msg;
    }
}
