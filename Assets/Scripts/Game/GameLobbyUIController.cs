using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class GameLobbyUIController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_Text _txtRole;
    [SerializeField] private TMP_Text _txtPlayers;
    [SerializeField] private TMP_Text _txtState;
    [SerializeField] private TMP_Text _txtCountdown;

    [SerializeField] private Button _btnReady;
    [SerializeField] private TMP_Text _btnReadyText; // Btn_Ready 안의 TMP_Text를 연결
    [SerializeField] private Button _btnStart;
    [SerializeField] private Button _btnLeave;

    [Header("Config")]
    [SerializeField] private string _mainMenuSceneName = "MainMenu";

    private GameSessionController _session;
    private bool _subscribed;
    private E_GameSessionState _lastState = (E_GameSessionState)255;
    private bool _inputEnabled;

    private bool _warnedMissingMyReady;

    private void OnEnable()
    {
        _session = GameSessionController.Instance;
        if (_session == null)
        {
            Debug.LogWarning("[GameLobbyUI] fallback 발생: GameSessionController.Instance is null.");
        }

        if (_btnReady == null || _btnStart == null || _btnLeave == null)
        {
            Debug.LogWarning("[GameLobbyUI] fallback 발생: button references missing.");
            return;
        }

        _btnReady.onClick.AddListener(OnClickReady);
        _btnStart.onClick.AddListener(OnClickStart);
        _btnLeave.onClick.AddListener(OnClickLeave);

        TrySubscribeReadyList();
        ForceRefreshUI();

        var nm = NetworkManager.Singleton;
        if (nm != null)
            Debug.Log($"[UI] localClientId={nm.LocalClientId}, isHost={nm.IsHost}");
    }

    private void OnDisable()
    {
        if (_btnReady != null) _btnReady.onClick.RemoveListener(OnClickReady);
        if (_btnStart != null) _btnStart.onClick.RemoveListener(OnClickStart);
        if (_btnLeave != null) _btnLeave.onClick.RemoveListener(OnClickLeave);

        UnsubscribeReadyList();
    }

    private void Update()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || _session == null)
        {
            SetTextSafe(_txtRole, "Role: -");
            SetTextSafe(_txtPlayers, "Players: -");
            SetTextSafe(_txtState, "State: -");
            SetTextSafe(_txtCountdown, "");
            SetButtonVisible(_btnReady, false);
            SetButtonVisible(_btnStart, false);
            SetButtonInteractable(_btnLeave, true);
            return;
        }

        // 구독이 늦게 되는 케이스(씬 스폰 타이밍) 처리
        if (!_subscribed)
            TrySubscribeReadyList();

        bool isHost = nm.IsHost;

        SetTextSafe(_txtRole, isHost ? "Host" : "Client");

        var st = _session.State;
        if (st != _lastState)
        {
            _lastState = st;
            OnStateChanged(st);
        }

        SetTextSafe(_txtState, $"State: {st}");

        // Players + Ready 표시
        SetTextSafe(_txtPlayers, BuildPlayersReadyText(nm));

        // Countdown 표시 + 버튼 숨김 처리
        if (st == E_GameSessionState.Countdown)
        {
            double remain = _session.StartAtServerTime - nm.ServerTime.Time;
            if (remain < 0) remain = 0;
            int sec = Mathf.CeilToInt((float)remain);
            SetTextSafe(_txtCountdown, $"Countdown: {sec}");

            // 레이스 시작(입력 enable)은 여기서 remain<=0 기준으로 처리하면 됨
            // 예: PlayerInputGate.SetEnabled(remain <= 0);
        }
        else
        {
            SetTextSafe(_txtCountdown, "");
        }

        // 버튼 표시/활성 정책
        bool sessionSpawned = _session.IsSpawned;
        if (!sessionSpawned)
        {
            Debug.LogWarning("[GameLobbyUI] fallback 발생: session not spawned yet.");
        }
        bool isLobby = (st == E_GameSessionState.Lobby);

        // Start 수행되면(Countdown/Running) Ready/Start 버튼은 둘 다 숨김
        if (!isLobby)
        {
            SetButtonVisible(_btnReady, false);
            SetButtonVisible(_btnStart, false);
            SetButtonInteractable(_btnLeave, true);
            return;
        }

        // Lobby 상태일 때만 버튼 제어
        // Host: Ready 버튼 숨김
        // Client: Start 버튼 숨김
        SetButtonVisible(_btnReady, !isHost);
        SetButtonVisible(_btnStart, isHost);

        // Ready 텍스트 즉시 반영(내 상태 기준)
        if (!isHost)
            RefreshReadyButtonText(nm.LocalClientId);

        // Ready 버튼은 Client만, 세션 스폰 이후 가능
        bool hasMyEntry = _session.TryGetMyReady(nm.LocalClientId, out _);
        bool canReady = sessionSpawned && !isHost && hasMyEntry;
        SetButtonInteractable(_btnReady, canReady);

        // Start 버튼은 Host만, 모든 Client Ready면 활성
        bool canStart = sessionSpawned && isHost && _session.IsAllClientsReady();
        SetButtonInteractable(_btnStart, canStart);

        SetButtonInteractable(_btnLeave, true);
    }

    // ----------------------------
    // ReadyList UI 즉시 반영
    // ----------------------------
    private void TrySubscribeReadyList()
    {
        if (_session == null) return;
        if (!_session.IsSpawned) return;
        if (_session.ReadyList == null) return;
        if (_subscribed) return;

        _session.ReadyList.OnListChanged += OnReadyListChanged;
        _subscribed = true;

        if (_session.ReadyList.Count > 0)
        {
            Debug.Log($"[UI] readyListCount={_session.ReadyList.Count}");
        }
    }

    private void UnsubscribeReadyList()
    {
        if (_session == null) return;
        if (_session.ReadyList == null) return;
        if (!_subscribed) return;

        _session.ReadyList.OnListChanged -= OnReadyListChanged;
        _subscribed = false;
    }

    private void OnReadyListChanged(NetworkListEvent<ReadyEntry> e)
    {
        Debug.Log($"[GameSession] OnReadyListChanged on client={NetworkManager.Singleton.LocalClientId} isServer={NetworkManager.Singleton.IsServer} type={e.Type}");

        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (_session != null && _session.IsSpawned && _session.ReadyList != null)
        {
            Debug.Log($"[UI] readyListCount={_session.ReadyList.Count}");
        }

        // Ready 변경이 일어나는 즉시 UI 갱신
        SetTextSafe(_txtPlayers, BuildPlayersReadyText(nm));

        // Client라면 버튼 텍스트 즉시 반영
        if (!nm.IsHost)
            RefreshReadyButtonText(nm.LocalClientId);

        // Host라면 Start 버튼 활성 조건 즉시 갱신
        if (nm.IsHost)
            SetButtonInteractable(_btnStart, _session.IsSpawned && _session.State == E_GameSessionState.Lobby && _session.IsAllClientsReady());
    }

    private void RefreshReadyButtonText(ulong myClientId)
    {
        if (_btnReadyText == null) return;
        if (_session == null || !_session.IsSpawned) return;

        if (_session.TryGetMyReady(myClientId, out bool myReady))
        {
            _btnReadyText.text = myReady ? "Unready" : "Ready";
            _warnedMissingMyReady = false;
            return;
        }

        // 아직 동기화가 안 온 상태(정상적으로도 발생 가능)
        _btnReadyText.text = "Sync...";

        if (!_warnedMissingMyReady)
        {
            Debug.LogWarning("[GameLobbyUI] Ready fallback 발생: my ready entry not found yet (sync pending).");
            _warnedMissingMyReady = true;
        }
    }

    // ----------------------------
    // State -> Input Enable
    // ----------------------------
    private void OnStateChanged(E_GameSessionState st)
    {
        if (st == E_GameSessionState.Running)
        {
            EnableLocalPlayerInputOnce();
        }
    }

    private void EnableLocalPlayerInputOnce()
    {
        if (_inputEnabled) return;

        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[GameLobbyUI] Input fallback 발생: NetworkManager.Singleton is null.");
            return;
        }

        // 내 로컬 플레이어의 PlayerInputGate 찾아서 Enable
        // (플레이어 프리팹에 PlayerInputGate 붙어 있어야 함)
        foreach (var pc in FindObjectsByType<PlayerInputGate>(FindObjectsSortMode.None))
        {
            if (pc == null) continue;
            if (!pc.IsSpawned) continue;
            if (!pc.IsOwner) continue;

            pc.SetEnabled(true);
            _inputEnabled = true;
            return;
        }

        Debug.LogWarning("[GameLobbyUI] Input fallback 발생: local PlayerInputGate not found.");
    }

    // ----------------------------
    // Button handlers
    // ----------------------------
    private void OnClickReady()
    {
        if (!TryGetNet(out var nm)) return;

        if (nm.IsHost)
        {
            Debug.LogWarning("[GameLobbyUI] Ready fallback 발생: host should not have Ready button.");
            return;
        }

        if (_session == null)
        {
            Debug.LogWarning("[GameLobbyUI] Ready fallback 발생: session is null.");
            return;
        }

        if (!_session.IsSpawned)
        {
            Debug.LogWarning("[GameLobbyUI] Ready fallback: session not spawned yet.");
            return;
        }

        _session.ToggleReadyRpc();

        // 즉시 UI 반영(네트워크 반영 전에 화면만 먼저 바꾸지 않기 위해)
        // 여기서는 "네트워크 이벤트(OnListChanged)"가 곧 올 것이므로 별도 즉시 변경은 안 함.
    }

    private void OnClickStart()
    {
        if (!TryGetNet(out var nm)) return;

        if (!nm.IsHost)
        {
            Debug.LogWarning("[GameLobbyUI] Start fallback 발생: client should not have Start button.");
            return;
        }

        if (_session == null)
        {
            Debug.LogWarning("[GameLobbyUI] Start fallback 발생: session is null.");
            return;
        }

        if (!_session.IsSpawned)
        {
            Debug.LogWarning("[GameLobbyUI] Start fallback 발생: session not spawned yet.");
            return;
        }

        _session.StartRaceRpc();
        // Start 후 State가 Countdown으로 바뀌면 Update에서 버튼 숨김 처리됨
    }

    private void OnClickLeave()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[GameLobbyUI] Leave fallback 발생: NetworkManager.Singleton is null.");
            SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
            return;
        }

        nm.Shutdown();
        SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
    }

    // ----------------------------
    // UI helpers
    // ----------------------------
    private void ForceRefreshUI()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || _session == null) return;

        SetTextSafe(_txtPlayers, BuildPlayersReadyText(nm));
        if (!nm.IsHost) RefreshReadyButtonText(nm.LocalClientId);
    }

    private string BuildPlayersReadyText(NetworkManager nm)
    {
        if (_session == null || !_session.IsSpawned || _session.ReadyList == null)
            return $"Players: {nm.ConnectedClientsIds.Count}";

        ulong hostId = NetworkManager.ServerClientId;
        ulong myId = nm.LocalClientId;

        var sb = new StringBuilder(128);
        sb.AppendLine($"Players: {_session.ReadyList.Count}");

        for (int i = 0; i < _session.ReadyList.Count; i++)
        {
            var e = _session.ReadyList[i];

            bool isHost = (e.clientId == hostId);
            bool isMe = (e.clientId == myId);

            sb.Append(isHost ? "Host" : "Client");
            if (isMe) sb.Append(" (You)");

            sb.Append(" : ");
            sb.Append(e.isReady ? "Ready" : "NotReady");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private bool TryGetNet(out NetworkManager nm)
    {
        nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[GameLobbyUI] fallback 발생: NetworkManager.Singleton is null.");
            return false;
        }
        return true;
    }

    private void SetTextSafe(TMP_Text t, string s)
    {
        if (t != null) t.text = s;
    }

    private void SetButtonVisible(Button b, bool visible)
    {
        if (b == null) return;
        if (b.gameObject.activeSelf != visible)
            b.gameObject.SetActive(visible);
    }

    private void SetButtonInteractable(Button b, bool interactable)
    {
        if (b == null) return;
        b.interactable = interactable;
    }
}
