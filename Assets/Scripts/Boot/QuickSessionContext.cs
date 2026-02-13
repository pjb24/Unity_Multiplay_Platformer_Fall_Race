using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class QuickSessionContext : MonoBehaviour
{
    // ============================
    // Singleton / Types
    // ============================
    public static QuickSessionContext Instance => _instance;

    public enum E_RelayProtocol
    {
        DTLS = 0,
        UDP = 1,
    }

    public readonly struct SessionResult
    {
        public readonly bool ok;
        public readonly string failCode;
        public readonly string message;
        public readonly string relayJoinCode;

        public SessionResult(bool ok, string failCode, string message, string relayJoinCode)
        {
            this.ok = ok;
            this.failCode = failCode;
            this.message = message;
            this.relayJoinCode = relayJoinCode;
        }

        public static SessionResult Ok(string joinCode) => new SessionResult(true, null, null, joinCode);
        public static SessionResult Fail(string code, string msg) => new SessionResult(false, code, msg, null);
    }

    // ============================
    // Config (Inspector)
    // ============================
    [Header("Config")]
    [SerializeField] private int _maxPlayers = 4;
    public int MaxPlayers => _maxPlayers;

    [SerializeField] private float _lobbyPollIntervalSec = 4f;     // 권장 3~5
    [SerializeField] private float _heartbeatIntervalSec = 15f;    // 권장 12~15 (Host만)
    [SerializeField] private E_RelayProtocol _relayProtocol = E_RelayProtocol.DTLS;
    [SerializeField] private string _gameSceneName = "Game";

    // Client가 Game씬에서도 Poll을 유지할지(선택: lock/kick 감지)
    [SerializeField] private bool _clientKeepPollingInGame = false;

    /// <summary>
    /// 현재 로컬 사용자가 최근 세션 진입 시도에서 사용한 이름입니다.
    /// </summary>
    public string LocalUsername { get; private set; }

    // ============================
    // State
    // ============================
    private static QuickSessionContext _instance;

    private Lobby _joinedLobby;
    private CancellationTokenSource _pollCts;
    private CancellationTokenSource _heartbeatCts;

    private Task _pollTask;
    private Task _heartbeatTask;

    // Lobby Data Keys
    private const string K_RelayJoinCode = "relayJoinCode";
    private const string K_HostPlayerId = "relayHost";

    // 중복 호출 가드(락/언락/리브/셧다운 등 공용)
    private bool _busy;

    // ============================
    // Unity Messages
    // ============================
    private void Awake()
    {
        // DDOL + 중복 제거
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // ============================
    // Public Entry (External API)
    // ============================
    public async Task<SessionResult> TryJoinAsClientThenEnterGameAsync(string username)
    {
        LocalUsername = SanitizeLocalUsername(username);
        var res = await JoinLobbyAsClientAsync(LocalUsername);
        if (!res.ok)
            Debug.LogWarning($"[QuickSession] fallback 발생: TryJoinAsClient failed: {res.failCode} / {res.message}");
        return res;
    }

    public async Task<SessionResult> TryStartAsHostThenEnterGameAsync(string username)
    {
        LocalUsername = SanitizeLocalUsername(username);
        var res = await CreateLobbyAsHostAsync(LocalUsername);
        if (!res.ok)
            Debug.LogWarning($"[QuickSession] fallback 발생: TryStartAsHost failed: {res.failCode} / {res.message}");
        return res;
    }

    // ============================
    // Leave / Shutdown Policy
    // ============================
    /// <summary>
    /// Client Leave:
    /// - Poll 종료(선택: 여기서는 항상 종료)
    /// - Lobby에서 플레이어 제거(RemovePlayerAsync)
    /// - 실패/폴백은 Warning 로그
    /// </summary>
    public async Task LeaveLobbyAsync(string reason)
    {
        if (_busy)
        {
            Debug.LogWarning($"[QuickSession] LeaveLobby fallback 발생: busy. reason={reason}");
            return;
        }

        _busy = true;
        try
        {
            // Poll은 클라에서 종료(요구사항)
            StopPolling();
            await AwaitLoopEndAsync(_pollTask, "[QuickSession] LeaveLobby Poll stop fallback 발생");
            _pollTask = null;

            if (_joinedLobby == null)
            {
                Debug.LogWarning($"[QuickSession] LeaveLobby fallback 발생: joinedLobby is null. reason={reason}");
                return;
            }

            if (!EnsureAuthReady())
            {
                Debug.LogWarning($"[QuickSession] LeaveLobby fallback 발생: not authenticated. reason={reason}");
                return;
            }

            string lobbyId = _joinedLobby.Id;
            string myId = AuthenticationService.Instance.PlayerId;

            if (string.IsNullOrEmpty(lobbyId))
            {
                Debug.LogWarning($"[QuickSession] LeaveLobby fallback 발생: lobbyId empty. reason={reason}");
                return;
            }

            if (string.IsNullOrEmpty(myId))
            {
                Debug.LogWarning($"[QuickSession] LeaveLobby fallback 발생: playerId empty. reason={reason}");
                return;
            }

            // Host가 여기로 들어오면 정책 위반(Host는 ShutdownSessionAsHostAsync로만)
            if (IsMeHost(_joinedLobby, myId))
            {
                Debug.LogWarning($"[QuickSession] LeaveLobby fallback 발생: host tried LeaveLobbyAsync. Use ShutdownSessionAsHostAsync. reason={reason}");
                return;
            }

            try
            {
                await LobbyService.Instance.RemovePlayerAsync(lobbyId, myId);
                Debug.Log($"[QuickSession] LeaveLobby done. reason={reason}, lobbyId={lobbyId}, playerId={myId}");
            }
            catch (LobbyServiceException e)
            {
                Debug.LogWarning($"[QuickSession] LeaveLobby fallback 발생: lobby error {e.Reason} / {e.Message} (reason={reason})");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuickSession] LeaveLobby fallback 발생: {e.GetType().Name} / {e.Message} (reason={reason})");
            }
            finally
            {
                // 로컬 상태 정리
                _joinedLobby = null;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Host Leave(초기 버전):
    /// - Heartbeat 중단
    /// - Poll 중단
    /// - Host면 DeleteLobbyAsync 허용(여기서만)
    /// - _joinedLobby=null
    /// - 실패/폴백은 Warning 로그
    /// </summary>
    public async Task ShutdownSessionAsHostAsync(string reason)
    {
        if (_busy)
        {
            Debug.LogWarning($"[QuickSession] ShutdownSession fallback 발생: busy. reason={reason}");
            return;
        }

        _busy = true;
        try
        {
            if (_joinedLobby == null)
            {
                Debug.LogWarning($"[QuickSession] ShutdownSession fallback 발생: joinedLobby is null. reason={reason}");
                return;
            }

            if (!EnsureAuthReady())
            {
                Debug.LogWarning($"[QuickSession] ShutdownSession fallback 발생: not authenticated. reason={reason}");
                return;
            }

            string myId = AuthenticationService.Instance.PlayerId;
            if (!IsMeHost(_joinedLobby, myId))
            {
                Debug.LogWarning($"[QuickSession] ShutdownSession fallback 발생: non-host tried shutdown. reason={reason}");
                return;
            }

            string lobbyId = _joinedLobby.Id;
            if (string.IsNullOrEmpty(lobbyId))
            {
                Debug.LogWarning($"[QuickSession] ShutdownSession fallback 발생: lobbyId empty. reason={reason}");
                return;
            }

            // 1) Stop Poll
            StopPolling();
            await AwaitLoopEndAsync(_pollTask, "[QuickSession] ShutdownSession Poll stop fallback 발생");
            _pollTask = null;

            // 2) Stop Heartbeat (Host만)
            StopHeartbeat();
            await AwaitLoopEndAsync(_heartbeatTask, "[QuickSession] ShutdownSession Heartbeat stop fallback 발생");
            _heartbeatTask = null;

            // 3) Delete Lobby (여기서만 허용)
            try
            {
                await LobbyService.Instance.DeleteLobbyAsync(lobbyId);
                Debug.Log($"[QuickSession] Session shutdown: lobby deleted. reason={reason}, lobbyId={lobbyId}");
            }
            catch (LobbyServiceException e)
            {
                Debug.LogWarning($"[QuickSession] ShutdownSession DeleteLobby fallback 발생: lobby error {e.Reason} / {e.Message} (reason={reason}, lobbyId={lobbyId})");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuickSession] ShutdownSession DeleteLobby fallback 발생: {e.GetType().Name} / {e.Message} (reason={reason}, lobbyId={lobbyId})");
            }
            finally
            {
                _joinedLobby = null;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    // ============================
    // Host-only Lobby Lock/Unlock (UGS Lobby Update)
    // - DeleteLobby 금지(게임 중 로비 유지)
    // - Heartbeat 유지(로비 생존)
    // - Poll: 선택
    // ============================
    public async Task LockLobbyAsync(string reason, bool stopPoll = true)
    {
        if (_busy)
        {
            Debug.LogWarning($"[QuickSession] LockLobby fallback 발생: busy. reason={reason}");
            return;
        }

        _busy = true;

        try
        {
            if (_joinedLobby == null)
            {
                Debug.LogWarning($"[QuickSession] LockLobby fallback 발생: joinedLobby is null. reason={reason}");
                return;
            }

            if (!EnsureAuthReady())
            {
                Debug.LogWarning($"[QuickSession] LockLobby fallback 발생: not authenticated. reason={reason}");
                return;
            }

            string myId = AuthenticationService.Instance.PlayerId;
            if (!IsMeHost(_joinedLobby, myId))
                return;

            // Poll은 선택적으로 중단
            if (stopPoll)
            {
                StopPolling();
                await AwaitLoopEndAsync(_pollTask, "[QuickSession] Poll stop fallback 발생");
                _pollTask = null;
            }

            // Heartbeat는 유지 (절대 StopHeartbeat 하지 마라)

            await TrySetLobbyLockedAsync(true, reason);
            Debug.Log($"[QuickSession] Lobby locked. reason={reason}, lobbyId={_joinedLobby?.Id}");

            // Host Heartbeat 항상 유지 체크 + 복구
            EnsureHeartbeatRunning_HostOnly($"lock_{reason}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuickSession] LockLobby fallback 발생: {e.GetType().Name} / {e.Message} (reason={reason})");
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task UnlockLobbyAsync(string reason, bool startPoll = false)
    {
        if (_busy)
        {
            Debug.LogWarning($"[QuickSession] UnlockLobby fallback 발생: busy. reason={reason}");
            return;
        }

        _busy = true;

        try
        {
            if (_joinedLobby == null)
            {
                Debug.LogWarning($"[QuickSession] UnlockLobby fallback 발생: joinedLobby is null. reason={reason}");
                return;
            }

            if (!EnsureAuthReady())
            {
                Debug.LogWarning($"[QuickSession] UnlockLobby fallback 발생: not authenticated. reason={reason}");
                return;
            }

            string myId = AuthenticationService.Instance.PlayerId;
            if (!IsMeHost(_joinedLobby, myId))
                return;

            await TrySetLobbyLockedAsync(false, reason);
            Debug.Log($"[QuickSession] Lobby unlocked. reason={reason}, lobbyId={_joinedLobby?.Id}");

            // Poll 재개는 선택
            if (startPoll && _joinedLobby != null && !string.IsNullOrEmpty(_joinedLobby.Id))
            {
                StartPolling(_joinedLobby.Id);
            }

            // Host Heartbeat 항상 유지 체크 + 복구
            EnsureHeartbeatRunning_HostOnly($"unlock_{reason}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuickSession] UnlockLobby fallback 발생: {e.GetType().Name} / {e.Message} (reason={reason})");
        }
        finally
        {
            _busy = false;
        }
    }

    // Lock/Unlock 내부 UpdateLobbyAsync를 “Host만 + LobbyId 확보”로 더 안전하게
    private async Task TrySetLobbyLockedAsync(bool locked, string reason)
    {
        if (_joinedLobby == null)
        {
            Debug.LogWarning($"[QuickSession] SetLocked fallback 발생: joinedLobby is null. locked={locked}, reason={reason}");
            return;
        }

        string lobbyId = _joinedLobby.Id;
        if (string.IsNullOrEmpty(lobbyId))
        {
            Debug.LogWarning($"[QuickSession] SetLocked fallback 발생: lobbyId is empty. locked={locked}, reason={reason}");
            return;
        }

        try
        {
            var opts = new UpdateLobbyOptions
            {
                IsLocked = locked
            };

            _joinedLobby = await LobbyService.Instance.UpdateLobbyAsync(lobbyId, opts);

            if (_joinedLobby == null)
            {
                Debug.LogWarning($"[QuickSession] SetLocked fallback 발생: UpdateLobby returned null. locked={locked}, reason={reason}");
            }
        }
        catch (LobbyServiceException e)
        {
            Debug.LogWarning($"[QuickSession] SetLocked fallback 발생: lobby error {e.Reason} / {e.Message} (locked={locked}, reason={reason})");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuickSession] SetLocked fallback 발생: {e.GetType().Name} / {e.Message} (locked={locked}, reason={reason})");
        }
    }

    // Host Heartbeat 실수 중단 감지 + 복구
    private void EnsureHeartbeatRunning_HostOnly(string reason)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsHost) return;

        if (_joinedLobby == null) return;

        // CTS가 없다면 Heartbeat가 꺼진 상태로 본다.
        if (_heartbeatCts != null) return;

        string lobbyId = _joinedLobby.Id;
        if (string.IsNullOrEmpty(lobbyId))
        {
            Debug.LogWarning($"[QuickSession] Heartbeat fallback 발생: lobbyId empty. cannot restart. reason={reason}");
            return;
        }

        Debug.LogWarning($"[QuickSession] Heartbeat fallback 발생: heartbeat not running. restart. reason={reason}");
        StartHeartbeat(lobbyId);
    }

    // ============================
    // Netcode Callbacks
    // ============================
    private async void OnServerStarted()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[QuickSession] OnServerStarted fallback 발생: NetworkManager.Singleton is null.");
            return;
        }

        nm.OnServerStarted -= OnServerStarted;

        // 씬 전환 직전: Host는 Poll만 중단 (Heartbeat 유지)
        try
        {
            await StopHostLobbyApiBeforeSceneLoadAsync("host_scene_load");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuickSession] StopHostLobbyApiBeforeSceneLoad fallback 발생: {e.GetType().Name} / {e.Message}");
        }

        // Host가 네트워크 씬 로드 지휘
        nm.SceneManager.LoadScene(_gameSceneName, LoadSceneMode.Single);
    }

    // Client Poll 중단을 옵션으로 만들기 (OnClientConnected 수정)
    private async void OnClientConnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[QuickSession] OnClientConnected fallback 발생: NetworkManager.Singleton is null.");
            return;
        }

        if (clientId != nm.LocalClientId) return;

        nm.OnClientConnectedCallback -= OnClientConnected;

        // 여기서 SceneManager.LoadScene(_gameSceneName) 절대 하지 마라.
        // Host가 씬 로드하면 자동으로 전환됨.

        // Client Poll 운용:
        // - 기본: Game 씬 들어가면 Poll 완전 중단(불필요)
        // - 옵션: Lock/Kick 감지하려면 유지
        if (!_clientKeepPollingInGame)
        {
            try
            {
                await StopPollFullyAsync("client_connected");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuickSession] StopPollFully fallback 발생: {e.GetType().Name} / {e.Message}");
            }
        }
        else
        {
            Debug.Log("[QuickSession] Client keeps polling for lobby signals (lock/kick).");
        }
    }

    // ============================
    // Host Flow
    // ============================
    public async Task<SessionResult> CreateLobbyAsHostAsync(string username)
    {
        if (!EnsureAuthReady())
        {
            Debug.LogWarning("[QuickSession] Host fallback 발생: not authenticated.");
            return SessionResult.Fail("not_authenticated", "auth not ready");
        }

        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[QuickSession] Host fallback 발생: NetworkManager.Singleton is null.");
            return SessionResult.Fail("no_network_manager", "NetworkManager missing");
        }

        var transport = nm.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogWarning("[QuickSession] Host fallback 발생: UnityTransport missing.");
            return SessionResult.Fail("no_transport", "UnityTransport missing");
        }

        try
        {
            // Relay Allocation (host 제외 연결 수 = maxPlayers - 1)
            int maxConnections = Mathf.Max(0, _maxPlayers - 1);
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            string relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            // Lobby Create + Data 저장 (relayJoinCode)
            var options = new CreateLobbyOptions
            {
                IsPrivate = false,
                Player = new Player(AuthenticationService.Instance.PlayerId, data: new Dictionary<string, PlayerDataObject>
                {
                    { "name", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, username) }
                }),
                Data = new Dictionary<string, DataObject>
                {
                    { K_HostPlayerId, new DataObject(DataObject.VisibilityOptions.Member, AuthenticationService.Instance.PlayerId) },
                    { K_RelayJoinCode, new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode) },
                }
            };

            string lobbyName = $"QS_{UnityEngine.Random.Range(1000, 9999)}";
            _joinedLobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, _maxPlayers, options);

            // Relay 정보로 UnityTransport의 네트워크 경로 구성
            // DTLS 사용(기본 권장)
            string protocol = GetRelayProtocolString();
            transport.SetRelayServerData(BuildRelayServerDataFromAllocation(allocation, protocol));

            // 서버 시작 확인 후 씬 전환 (한 프레임 미뤄도 됨)
            nm.OnServerStarted += OnServerStarted;

            // NGO StartHost
            if (!nm.StartHost())
            {
                Debug.LogWarning("[QuickSession] Host fallback 발생: NetworkManager.StartHost failed.");
                return SessionResult.Fail("start_host_failed", "StartHost failed");
            }

            // Host만 Heartbeat + Poll 시작
            StartHeartbeat(_joinedLobby.Id);
            StartPolling(_joinedLobby.Id);

            return SessionResult.Ok(relayJoinCode);
        }
        catch (LobbyServiceException e)
        {
            Debug.LogWarning($"[QuickSession] Host fallback 발생: lobby error {e.Reason} / {e.Message}");
            return SessionResult.Fail("lobby_error", e.Message);
        }
        catch (RelayServiceException e)
        {
            Debug.LogWarning($"[QuickSession] Host fallback 발생: relay error {e.Reason} / {e.Message}");
            return SessionResult.Fail("relay_error", e.Message);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuickSession] Host fallback 발생: exception {e.GetType().Name} / {e.Message}");
            return SessionResult.Fail("exception", e.Message);
        }
    }


    // ============================
    // Client Flow (Quick Join)
    // ============================
    public async Task<SessionResult> JoinLobbyAsClientAsync(string username)
    {
        if (!EnsureAuthReady())
        {
            Debug.LogWarning("[QuickSession] Client fallback 발생: not authenticated.");
            return SessionResult.Fail("not_authenticated", "auth not ready");
        }

        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[QuickSession] Client fallback 발생: NetworkManager.Singleton is null.");
            return SessionResult.Fail("no_network_manager", "NetworkManager missing");
        }

        var transport = nm.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogWarning("[QuickSession] Client fallback 발생: UnityTransport missing.");
            return SessionResult.Fail("no_transport", "UnityTransport missing");
        }

        try
        {
            // Quick Join Lobby
            var joinOptions = new QuickJoinLobbyOptions
            {
                Player = new Player(AuthenticationService.Instance.PlayerId, data: new Dictionary<string, PlayerDataObject>
                {
                    { "name", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, username) }
                })
            };

            _joinedLobby = await LobbyService.Instance.QuickJoinLobbyAsync(joinOptions);

            // Lobby Data에서 relayJoinCode 확인
            if (_joinedLobby.Data == null || !_joinedLobby.Data.TryGetValue(K_RelayJoinCode, out var codeObj))
            {
                Debug.LogWarning("[QuickSession] Client fallback 발생: relayJoinCode missing in lobby data.");
                return SessionResult.Fail("missing_relay_code", "relayJoinCode missing");
            }

            string relayJoinCode = codeObj.Value;
            if (string.IsNullOrEmpty(relayJoinCode))
            {
                Debug.LogWarning("[QuickSession] Client fallback 발생: relayJoinCode is empty.");
                return SessionResult.Fail("empty_relay_code", "relayJoinCode empty");
            }

            // Relay Join
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);

            // Relay 정보로 UnityTransport의 네트워크 경로 구성
            string protocol = GetRelayProtocolString();
            transport.SetRelayServerData(BuildRelayServerDataFromJoinAllocation(joinAllocation, protocol));

            nm.OnClientConnectedCallback += OnClientConnected;

            // NGO StartClient
            if (!nm.StartClient())
            {
                Debug.LogWarning("[QuickSession] Client fallback 발생: NetworkManager.StartClient failed.");
                return SessionResult.Fail("start_client_failed", "StartClient failed");
            }

            // Poll 시작(클라에서도 lobby 상태 변화 감시용)
            StartPolling(_joinedLobby.Id);

            return SessionResult.Ok(relayJoinCode);
        }
        catch (LobbyServiceException e)
        {
            Debug.LogWarning($"[QuickSession] Client fallback 발생: lobby error {e.Reason} / {e.Message}");
            return SessionResult.Fail("lobby_error", e.Message);
        }
        catch (RelayServiceException e)
        {
            Debug.LogWarning($"[QuickSession] Client fallback 발생: relay error {e.Reason} / {e.Message}");
            return SessionResult.Fail("relay_error", e.Message);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuickSession] Client fallback 발생: exception {e.GetType().Name} / {e.Message}");
            return SessionResult.Fail("exception", e.Message);
        }
    }

    // ============================
    // Poll/Heartbeat public controls
    // ============================
    public async Task StopPollFullyAsync(string reason)
    {
        StopPolling();
        await AwaitLoopEndAsync(_pollTask, "[QuickSession] Poll stop fallback 발생");
        _pollTask = null;

        Debug.Log($"[QuickSession] Poll stopped fully. reason={reason}");
    }

    public async Task StopHostLobbyApiBeforeSceneLoadAsync(string reason)
    {
        // Poll은 끊어도 됨
        StopPolling();
        await AwaitLoopEndAsync(_pollTask, "[QuickSession] Poll stop fallback 발생");
        _pollTask = null;

        // Heartbeat는 끊지 마라 (로비 생존용)
        Debug.Log($"[QuickSession] Host stopped POLL only. reason={reason}");
    }

    // ============================
    // Heartbeat / Poll (no UI refs)
    // ============================
    private void StartHeartbeat(string lobbyId)
    {
        StopHeartbeat(); // 기존 루프 완전 중단
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTask = HeartbeatLoopAsync(lobbyId, _heartbeatCts.Token);
    }

    private void StopHeartbeat()
    {
        if (_heartbeatCts == null) return;

        _heartbeatCts.Cancel();
        _heartbeatCts.Dispose();
        _heartbeatCts = null;

        // 여기서 await는 못 하니, 완전 중단은 Cleanup에서 await 처리
    }

    private async Task HeartbeatLoopAsync(string lobbyId, CancellationToken ct)
    {
        // Host만 호출되어야 함
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuickSession] Heartbeat fallback 발생: {e.GetType().Name} / {e.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSec), ct);
            }
            catch { /* ignore */ }
        }
    }

    private void StartPolling(string lobbyId)
    {
        StopPolling(); // 기존 루프 완전 중단
        _pollCts = new CancellationTokenSource();
        _pollTask = PollLoopAsync(lobbyId, _pollCts.Token);
    }

    private void StopPolling()
    {
        if (_pollCts == null) return;

        _pollCts.Cancel();
        _pollCts.Dispose();
        _pollCts = null;

        // 여기서 await는 못 하니, 완전 중단은 Cleanup에서 await 처리
    }

    private async Task PollLoopAsync(string lobbyId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _joinedLobby = await LobbyService.Instance.GetLobbyAsync(lobbyId);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuickSession] Poll fallback 발생: {e.GetType().Name} / {e.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_lobbyPollIntervalSec), ct);
            }
            catch { /* ignore */ }
        }
    }

    // ============================
    // Cleanup Helpers
    // ============================
    private static async Task AwaitLoopEndAsync(Task task, string warnPrefix)
    {
        if (task == null) return;

        try
        {
            // Cancel된 루프가 정상적으로 빠져나오는지 확인
            await task;
        }
        catch (OperationCanceledException)
        {
            // 정상
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{warnPrefix}: {e.GetType().Name} / {e.Message}");
        }
    }

    private async Task TryDeleteLobbyIfHostAsync(string reason)
    {
        if (_joinedLobby == null) return;
        if (!EnsureAuthReady())
        {
            Debug.LogWarning("[QuickSession] DeleteLobby fallback 발생: not authenticated.");
            return;
        }

        string myId = AuthenticationService.Instance.PlayerId;
        if (string.IsNullOrEmpty(myId))
        {
            Debug.LogWarning("[QuickSession] DeleteLobby fallback 발생: playerId empty.");
            return;
        }

        bool isHost = IsMeHost(_joinedLobby, myId);
        if (!isHost) return;

        try
        {
            await LobbyService.Instance.DeleteLobbyAsync(_joinedLobby.Id);
            Debug.Log($"[QuickSession] Lobby deleted. reason={reason}, lobbyId={_joinedLobby.Id}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[QuickSession] DeleteLobby fallback 발생: {e.GetType().Name} / {e.Message} (reason={reason})");
        }
    }

    // ============================
    // Host check
    // ============================
    private static bool IsMeHost(Lobby lobby, string myPlayerId)
    {
        if (lobby == null || string.IsNullOrEmpty(myPlayerId)) return false;

        // 1순위: Lobby.HostId (일반적으로 존재)
        if (!string.IsNullOrEmpty(lobby.HostId))
            return string.Equals(lobby.HostId, myPlayerId, StringComparison.Ordinal);

        // 2순위: Data에 저장한 relayHost 키
        if (lobby.Data != null &&
            lobby.Data.TryGetValue(K_HostPlayerId, out var obj) &&
            !string.IsNullOrEmpty(obj?.Value))
        {
            return string.Equals(obj.Value, myPlayerId, StringComparison.Ordinal);
        }

        Debug.LogWarning("[QuickSession] Host check fallback 발생: cannot determine host. Treat as non-host.");
        return false;
    }

    // ============================
    // Relay Helpers
    // ============================
    private string GetRelayProtocolString()
    {
        switch (_relayProtocol)
        {
            case E_RelayProtocol.DTLS: return "dtls";
            case E_RelayProtocol.UDP: return "udp";
            default:
                Debug.LogWarning($"[QuickSession] Relay protocol fallback 발생: invalid protocol={_relayProtocol}. Use dtls.");
                return "dtls";
        }
    }

    private static RelayServerData BuildRelayServerDataFromAllocation(Allocation allocation, string connectionType)
    {
        if (allocation == null)
            throw new ArgumentNullException(nameof(allocation));

        RelayServerEndpoint ep = PickEndpoint(allocation.ServerEndpoints, connectionType);

        // Host는 hostConnectionData = connectionData 로 넣는다.
        return new RelayServerData(
            host: ep.Host,
            port: (ushort)ep.Port,
            allocationId: allocation.AllocationIdBytes,
            connectionData: allocation.ConnectionData,
            hostConnectionData: allocation.ConnectionData,
            key: allocation.Key,
            isSecure: ep.Secure
        );
    }

    private static RelayServerData BuildRelayServerDataFromJoinAllocation(JoinAllocation joinAllocation, string connectionType)
    {
        if (joinAllocation == null)
            throw new ArgumentNullException(nameof(joinAllocation));

        RelayServerEndpoint ep = PickEndpoint(joinAllocation.ServerEndpoints, connectionType);

        // Client는 hostConnectionData = joinAllocation.HostConnectionData.
        return new RelayServerData(
            host: ep.Host,
            port: (ushort)ep.Port,
            allocationId: joinAllocation.AllocationIdBytes,
            connectionData: joinAllocation.ConnectionData,
            hostConnectionData: joinAllocation.HostConnectionData,
            key: joinAllocation.Key,
            isSecure: ep.Secure
        );
    }

    private static RelayServerEndpoint PickEndpoint(List<RelayServerEndpoint> endpoints, string connectionType)
    {
        if (endpoints == null || endpoints.Count == 0)
            throw new InvalidOperationException("Relay server endpoints are empty.");

        // endpoints 순서는 보장되지 않으니 직접 찾는다.
        for (int i = 0; i < endpoints.Count; i++)
        {
            RelayServerEndpoint e = endpoints[i];
            if (e != null && string.Equals(e.ConnectionType, connectionType, StringComparison.OrdinalIgnoreCase))
                return e;
        }

        Debug.LogWarning($"[LobbyController] Relay endpoint fallback 발생: connectionType '{connectionType}' not found. Using first endpoint.");
        return endpoints[0];
    }

    /// <summary>
    /// 표시 이름 입력값을 정리하고 비어 있으면 기본 이름을 생성합니다.
    /// </summary>
    private static string SanitizeLocalUsername(string rawName)
    {
        string trimmed = string.IsNullOrWhiteSpace(rawName) ? string.Empty : rawName.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return $"User{UnityEngine.Random.Range(1000, 9999)}";

        return trimmed;
    }

    // ============================
    // Auth Helper
    // ============================
    private bool EnsureAuthReady()
    {
        // Bootstrap에서 이미 Initialize + SignIn이 끝났다는 전제
        return AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn;
    }
}
