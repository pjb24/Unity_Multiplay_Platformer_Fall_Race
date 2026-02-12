using System;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class GameLobbyUIController : MonoBehaviour
{
    // ============================
    // UI (Inspector)
    // ============================
    [Header("UI")]
    [SerializeField] private TMP_Text _txtRole;
    [SerializeField] private TMP_Text _txtPlayers;
    [SerializeField] private TMP_Text _txtState;
    [SerializeField] private TMP_Text _txtCountdown;

    [SerializeField] private Button _btnReady;
    [SerializeField] private TMP_Text _btnReadyText; // Btn_Ready 안의 TMP_Text를 연결
    [SerializeField] private Button _btnStart;
    [SerializeField] private Button _btnLeave;

    // ============================
    // Config
    // ============================
    [Header("Config")]
    [SerializeField] private string _mainMenuSceneName = "MainMenu";

    // ============================
    // State
    // ============================
    private GameSessionController _session;

    private bool _subscribed;          // ReadyList 구독 여부
    private bool _stateSubscribed;     // State 구독 여부
    private bool _netSubscribed;   // NetworkManager 콜백 구독 여부

    private E_GameSessionState _lastState = (E_GameSessionState)255;
    private bool _inputEnabled;
    private bool _warnedMissingMyReady;
    private bool _leavingInProgress;

    // ============================
    // Unity Messages
    // ============================
    private void OnEnable()
    {
        // 늦게 스폰되는 케이스는 Update에서 재획득/구독
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

        TrySubscribeNetCallbacks();
        TrySubscribeState();      // State 구독
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

        UnsubscribeNetCallbacks();
        UnsubscribeState();       // State 구독 해제
        UnsubscribeReadyList();
    }

    private void Update()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            RenderNoSessionState();
            return;
        }

        if (!_netSubscribed)
            TrySubscribeNetCallbacks();

        // 세션이 늦게 생성/스폰되는 케이스 대응
        if (_session == null)
            _session = GameSessionController.Instance;

        if (_session == null)
        {
            RenderNoSessionState();
            return;
        }

        // 구독이 늦게 되는 케이스(씬 스폰 타이밍) 처리
        if (!_stateSubscribed)
            TrySubscribeState();

        if (!_subscribed)
            TrySubscribeReadyList();

        RenderRole(nm);
        RenderStateAndHandleTransition(nm);
        RenderPlayers(nm);
        RenderCountdown(nm);
        RenderButtons(nm);
    }

    // ============================
    // Net callbacks (Host 종료/Client 종료 감지)
    // ============================
    private void TrySubscribeNetCallbacks()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (_netSubscribed) return;

        nm.OnClientDisconnectCallback -= OnClientDisconnected_Net;
        nm.OnClientDisconnectCallback += OnClientDisconnected_Net;

        _netSubscribed = true;
    }

    private void UnsubscribeNetCallbacks()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (!_netSubscribed) return;

        nm.OnClientDisconnectCallback -= OnClientDisconnected_Net;
        _netSubscribed = false;
    }

    private void OnClientDisconnected_Net(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        // 내 로컬이 끊긴 경우만 처리
        if (clientId != nm.LocalClientId) return;

        // 내가 누른 Leave면 중복 처리 금지
        if (_leavingInProgress) return;

        // Host가 내려가서 세션이 끝난 케이스(클라에서 관찰)
        Debug.LogWarning("[Session] Disconnected -> session_end (host_left_or_network_end). Load MainMenu.");
        SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
    }

    // ============================
    // State Subscribe
    // ============================
    private void TrySubscribeState()
    {
        if (_session == null) return;
        if (!_session.IsSpawned) return;
        if (_stateSubscribed) return;

        _session.AddStateListener(OnSessionStateChanged);
        _stateSubscribed = true;
    }

    private void UnsubscribeState()
    {
        if (_session == null) return;
        if (!_stateSubscribed) return;

        _session.RemoveStateListener(OnSessionStateChanged);
        _stateSubscribed = false;
    }

    private void OnSessionStateChanged(E_GameSessionState prev, E_GameSessionState next)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || _session == null)
        {
            Debug.LogWarning("[GameLobbyUI] StateChanged fallback 발생: NetworkManager or session missing.");
            return;
        }

        Debug.Log($"[GameLobbyUI] StateChanged: {prev} -> {next}");

        // 기존 전환 처리 재사용
        _lastState = next;
        OnStateChanged(next);

        // Lobby 복귀 즉시 UI 복구 + 새로 들어온 Client(Ready=false)까지 바로 반영
        if (next == E_GameSessionState.Lobby)
        {
            // 다음 매치 대비: 입력 게이트 재설정 필요 시 여기서 초기화
            _inputEnabled = false;

            // Ready 버튼 텍스트 sync 경고 플래그 리셋
            _warnedMissingMyReady = false;

            ForceRefreshUI();
        }

        // Countdown/Running이면 즉시 버튼 숨김/비활성
        // Client: Ready 버튼 표시
        // Host: Start 버튼 비활성(= all ready 될 때만 활성)
        RenderButtons(nm);
    }

    // ============================
    // ReadyList Subscribe - UI 즉시 반영
    // ============================
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
        var nm = NetworkManager.Singleton;
        if (nm == null || _session == null) return;

        Debug.Log($"[GameSession] OnReadyListChanged on client={nm.LocalClientId} isServer={nm.IsServer} type={e.Type}");

        if (_session != null && _session.IsSpawned && _session.ReadyList != null)
        {
            Debug.Log($"[UI] readyListCount={_session.ReadyList.Count}");
        }

        // Ready 변경이 일어나는 즉시 UI 갱신
        SetTextSafe(_txtPlayers, BuildPlayersReadyText(nm));

        // Unlock 이후 새 Client가 들어오면 Ready=false로 추가됨 -> 즉시 버튼/텍스트 갱신
        if (!nm.IsHost)
            RefreshReadyButtonText(nm.LocalClientId);

        // Host의 Start 버튼은 Lobby에서 all-ready일 때만
        if (nm.IsHost)
        {
            bool canStart =
                _session.IsSpawned &&
                _session.State == E_GameSessionState.Lobby &&
                _session.IsAllClientsReady();

            SetButtonInteractable(_btnStart, canStart);
        }
    }

    // ============================
    // State Transition -> Input Enable
    // ============================
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

            pc.SetEnabled(true, E_InputGateReason.Run);
            _inputEnabled = true;
            return;
        }

        Debug.LogWarning("[GameLobbyUI] Input fallback 발생: local PlayerInputGate not found.");
    }

    // ============================
    // Button Handlers
    // ============================
    private void OnClickReady()
    {
        if (_leavingInProgress) return;

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

        // UI는 ReadyList OnListChanged에서 반영
    }

    private void OnClickStart()
    {
        if (_leavingInProgress) return;

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
        if (_leavingInProgress) return;
        _leavingInProgress = true;

        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[GameLobbyUI] Leave fallback 발생: NetworkManager.Singleton is null.");
            SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
            return;
        }

        // 입력/버튼 중복 방지
        SetButtonInteractable(_btnLeave, false);
        SetButtonInteractable(_btnReady, false);
        SetButtonInteractable(_btnStart, false);

        bool isHost = nm.IsHost;

        // ✅ UGS Lobby 정리 (반드시 Warning 로그 기반 폴백)
        // - Client: LeaveLobbyAsync(reason) (RemovePlayerAsync)
        // - Host: ShutdownSessionAsHostAsync(reason) (Stop heartbeat/poll + DeleteLobby 허용)
        _ = LeaveFlowAsync(isHost);

        // ✅ NGO 종료는 유지 (언제든 Leave 가능)
        try
        {
            nm.Shutdown();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameLobbyUI] Leave fallback 발생: NetworkManager.Shutdown exception {e.GetType().Name} / {e.Message}");
        }

        // 초기 버전 정책: Leave는 MainMenu로 복귀
        SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
    }

    private async Task LeaveFlowAsync(bool isHost)
    {
        var qs = QuickSessionContext.Instance;
        if (qs == null)
        {
            Debug.LogWarning("[GameLobbyUI] Leave fallback 발생: QuickSessionContext.Instance is null. UGS lobby cleanup skipped.");
            return;
        }

        try
        {
            if (isHost)
            {
                Debug.LogWarning("[Session] HostLeave -> session_end");

                // ⚠️ 이 함수는 QuickSessionContext에 구현되어 있어야 함.
                // 포함: StopHeartbeat + StopPoll + (Host면) DeleteLobby + joinedLobby null
                await qs.ShutdownSessionAsHostAsync("host_leave");
            }
            else
            {
                // ⚠️ 이 함수는 QuickSessionContext에 구현되어 있어야 함.
                // 포함: RemovePlayerAsync + Poll stop (or 내부에서 stop)
                await qs.LeaveLobbyAsync("client_leave");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameLobbyUI] Leave cleanup fallback 발생: {e.GetType().Name} / {e.Message} (isHost={isHost})");
        }
    }

    // ============================
    // Rendering (Update split)
    // ============================
    private void RenderNoSessionState()
    {
        SetTextSafe(_txtRole, "Role: -");
        SetTextSafe(_txtPlayers, "Players: -");
        SetTextSafe(_txtState, "State: -");
        SetTextSafe(_txtCountdown, "");
        SetButtonVisible(_btnReady, false);
        SetButtonVisible(_btnStart, false);
        SetButtonInteractable(_btnLeave, true);
    }

    private void RenderRole(NetworkManager nm)
    {
        SetTextSafe(_txtRole, nm.IsHost ? "Host" : "Client");
    }

    private void RenderStateAndHandleTransition(NetworkManager nm)
    {
        var st = _session.State;
        if (st != _lastState)
        {
            _lastState = st;
            OnStateChanged(st);
        }

        SetTextSafe(_txtState, $"State: {st}");
    }

    private void RenderPlayers(NetworkManager nm)
    {
        // Players + Ready 표시
        SetTextSafe(_txtPlayers, BuildPlayersReadyText(nm));
    }

    private void RenderCountdown(NetworkManager nm)
    {
        var st = _session.State;

        // Countdown 표시 + 버튼 숨김 처리
        if (st == E_GameSessionState.Countdown)
        {
            double remain = _session.StartAtServerTime - nm.ServerTime.Time;
            if (remain < 0) remain = 0;

            int sec = Mathf.CeilToInt((float)remain);
            SetTextSafe(_txtCountdown, $"Countdown: {sec}");
            return;
        }

        SetTextSafe(_txtCountdown, "");
    }

    private void RenderButtons(NetworkManager nm)
    {
        if (_leavingInProgress)
        {
            SetButtonVisible(_btnReady, false);
            SetButtonVisible(_btnStart, false);
            SetButtonInteractable(_btnLeave, false);
            return;
        }

        bool isHost = nm.IsHost;
        var st = _session.State;

        bool sessionSpawned = _session.IsSpawned;
        if (!sessionSpawned)
        {
            Debug.LogWarning("[GameLobbyUI] fallback 발생: session not spawned yet.");
        }

        bool isLobby = (st == E_GameSessionState.Lobby);

        // Countdown/Running이면 Ready/Start 숨김
        if (!isLobby)
        {
            SetButtonVisible(_btnReady, false);
            SetButtonVisible(_btnStart, false);
            SetButtonInteractable(_btnLeave, true);

            // 혹시 남아있던 interactable 상태 방지
            SetButtonInteractable(_btnStart, false);
            SetButtonInteractable(_btnReady, false);
            return;
        }

        // Lobby 상태에서만 표시
        // Host: Ready 버튼 숨김
        // Client: Start 버튼 숨김
        SetButtonVisible(_btnReady, !isHost);
        SetButtonVisible(_btnStart, isHost);

        // Client면 Ready 버튼의 텍스트 갱신
        if (!isHost)
            RefreshReadyButtonText(nm.LocalClientId);

        // Ready: Client만, 세션 스폰 이후 가능
        bool hasMyEntry = _session.TryGetMyReady(nm.LocalClientId, out _);
        bool canReady = sessionSpawned && !isHost && hasMyEntry;
        SetButtonInteractable(_btnReady, canReady);

        // Start: Host만, 모든 Client Ready면 활성
        bool canStart = sessionSpawned && isHost && _session.IsAllClientsReady();
        SetButtonInteractable(_btnStart, canStart);

        SetButtonInteractable(_btnLeave, true);
    }

    // ============================
    // UI Helpers
    // ============================
    private void ForceRefreshUI()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || _session == null) return;

        SetTextSafe(_txtRole, nm.IsHost ? "Host" : "Client");
        SetTextSafe(_txtPlayers, BuildPlayersReadyText(nm));
        
        // State도 초기 한번 맞춰둠
        SetTextSafe(_txtState, $"State: {_session.State}");
        SetTextSafe(_txtCountdown, "");

        if (!nm.IsHost) RefreshReadyButtonText(nm.LocalClientId);
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
