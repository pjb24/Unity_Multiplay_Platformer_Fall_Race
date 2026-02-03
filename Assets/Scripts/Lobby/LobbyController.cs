using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class LobbyController : MonoBehaviour
{
    [Header("UI - Lobby Info")]
    [SerializeField] private TMP_Text _lobbyNameText;
    [SerializeField] private TMP_Text _lobbyCodeText;
    [SerializeField] private TMP_Text _statusText;

    [Header("UI - Player Slots (size=4)")]
    [SerializeField] private TMP_Text[] _slotNameTexts = new TMP_Text[4];
    [SerializeField] private Image[] _slotHostIcons = new Image[4];

    [Header("UI - Buttons")]
    [SerializeField] private Button _startGameButton;
    [SerializeField] private Button _leaveButton;

    [Header("Config")]
    [SerializeField] private float _pollIntervalSeconds = 5f;
    [SerializeField, Range(10f, 25f)] private float _heartbeatIntervalSeconds = 12f;

    [Header("Optional - NGO Network Scene Load")]
    [SerializeField] private string _mainMenuSceneName = "MainMenu";

    [Header("Timeouts (seconds)")]
    [SerializeField] private float _reconnectTimeoutSeconds = 3f;
    [SerializeField] private float _pollTimeoutSeconds = 20f;
    [SerializeField] private float _updateTimeoutSeconds = 8f;
    [SerializeField] private float _leaveTimeoutSeconds = 3f;

    [Header("Relay")]
    [SerializeField] private string _relayJoinCodeKey = "relayJoinCode";
    [SerializeField] private string _gameStartedKey = "gameStarted";
    [SerializeField] private string _gameSceneKey = "gameScene";
    [SerializeField] private string _gameSceneName = "Game";

    [Header("Client Join Retry")]
    [SerializeField] private float _joinTimeoutSeconds = 8f;

    private bool _gameStartObserved;

    private bool _startGameInFlight;
    private bool _clientJoinInFlight;

    private Lobby _lobby;
    private string _playerId;
    private string _playerName;

    private float _hbT;

    private bool _pollInFlight;
    private bool _startInFlight;

    private CancellationTokenSource _cts;

    private float _nextJoinAttemptAt;
    private float _joinBackoffSeconds = 0f;
    private const float JoinBackoffMin = 2f;
    private const float JoinBackoffMax = 12f;

    private float _nextPollAt;
    private float _pollBackoffSeconds;
    private const float PollBackoffMin = 2f;
    private const float PollBackoffMax = 30f;

    private const string PrefKeyLobbyId = "lobby_id";
    private const string PrefKeyPlayerName = "player_name";

    private void Awake()
    {
        if (_statusText == null || _startGameButton == null || _leaveButton == null)
        {
            Debug.LogError("[LobbyController] Missing required UI references.");
            enabled = false;
            return;
        }

        if (_slotNameTexts == null || _slotNameTexts.Length != 4 ||
            _slotHostIcons == null || _slotHostIcons.Length != 4)
        {
            Debug.LogError("[LobbyController] Slot arrays must be size 4.");
            enabled = false;
            return;
        }

        _startGameButton.onClick.AddListener(OnClickStartGame);
        _leaveButton.onClick.AddListener(OnClickLeave);

        _cts = new CancellationTokenSource();
    }

    private void Start()
    {
        if (_startInFlight)
        {
            Debug.LogWarning("[LobbyController] Start fallback 발생: already running.");
            return;
        }

        _startInFlight = true;

        // 연결 전 UI 초기화
        _lobby = null;
        RefreshUI();
        SetInteractable(false);
        SetStatus("Connecting to lobby...");

        _ = StartAsync(_cts.Token);
    }

    private async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await EnsureSignedInAsync(ct);

            _playerId = AuthenticationService.Instance.PlayerId;
            _playerName = PlayerPrefs.GetString(PrefKeyPlayerName, string.Empty);

            // 1) MainMenu에서 넘어온 Lobby가 있으면 그걸 우선 사용
            // (QuickSessionContext.Instance.CurrentLobby 같은 static 컨텍스트)
            Lobby passedLobby = null;
            try
            {
                passedLobby = QuickSessionContext.Instance.CurrentLobby;
            }
            catch
            {
                // 의존성 없게: 타입이 없으면 그냥 무시
            }

            if (passedLobby != null && !string.IsNullOrEmpty(passedLobby.Id))
            {
                SaveLobbyId(passedLobby.Id);

                // passedLobby는 최신이 아닐 수 있으니 서버에서 다시 Get
                _lobby = await GetLobbySafeAsync(passedLobby.Id, ct);
                if (_lobby == null)
                {
                    Debug.LogWarning("[LobbyController] Start fallback 발생: passed lobby invalid. Trying saved lobby_id.");
                }
            }

            // 2) passedLobby가 없거나 실패하면 PlayerPrefs lobby_id로 복구
            if (_lobby == null)
            {
                string lobbyId = PlayerPrefs.GetString(PrefKeyLobbyId, string.Empty);
                if (string.IsNullOrWhiteSpace(lobbyId))
                {
                    Debug.LogWarning("[LobbyController] Start fallback 발생: no saved lobby_id.");
                    SetStatus("No lobby to reconnect. Go back to MainMenu.");
                    return;
                }

                _lobby = await ReconnectOrJoinByIdAsync(lobbyId, ct);
                if (_lobby == null)
                {
                    SetStatus("Reconnect failed. Lobby may be closed.");
                    ClearSavedLobby();
                    return;
                }

                SaveLobbyId(_lobby.Id);
            }

            // 연결 성공
            _hbT = 0f;

            if (_lobby != null && !IsHost())
                _ = ClientTryJoinGameIfStartedAsync(_cts.Token);

            _nextPollAt = Time.unscaledTime + _pollIntervalSeconds;
            _pollBackoffSeconds = 0f;

            SetStatus("Connected.");
            RefreshUI();
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("[LobbyController] Start fallback 발생: canceled/timeout.");
            SetStatus("Canceled / Timeout");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LobbyController] Start fallback 발생: {ex}");
            SetStatus("Failed. Check console.");
        }
        finally
        {
            SetInteractable(true);
            RefreshUI();
            _startInFlight = false;
        }
    }

    private async Task<Lobby> GetLobbySafeAsync(string lobbyId, CancellationToken externalCt)
    {
        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _reconnectTimeoutSeconds);

        try
        {
            Task<Lobby> req = LobbyService.Instance.GetLobbyAsync(lobbyId);
            return await WaitWithCancellation(req, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is LobbyServiceException || ex is OperationCanceledException)
        {
            Debug.LogWarning($"[LobbyController] GetLobbySafe fallback 발생: {ex.Message}");
            return null;
        }
    }

    private void Update()
    {
        if (_lobby == null) return;

        if (Time.unscaledTime >= _nextPollAt && !_pollInFlight)
        {
            _ = PollLobbyAsync(_cts.Token);
        }

        if (IsHost())
        {
            _hbT += Time.deltaTime;
            if (_hbT >= _heartbeatIntervalSeconds)
            {
                _hbT = 0f;
                _ = HeartbeatAsync(_cts.Token);
            }
        }

        // started 관측 후 Join 시도는 "쿨다운" 기반으로만 굴린다 (API 스팸 방지)
        if (_gameStartObserved && !IsHost() && !_clientJoinInFlight)
        {
            _ = ClientTryJoinGameIfStartedAsync(_cts.Token);
        }
    }

    private void OnDestroy()
    {
        _startGameButton.onClick.RemoveListener(OnClickStartGame);
        _leaveButton.onClick.RemoveListener(OnClickLeave);

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    private async void OnClickStartGame()
    {
        if (_startGameInFlight)
        {
            Debug.LogWarning("[LobbyController] StartGame fallback 발생: already in flight.");
            return;
        }

        if (_lobby == null)
        {
            Debug.LogWarning("[LobbyController] StartGame fallback 발생: lobby is null.");
            SetStatus("No lobby.");
            return;
        }

        if (!IsHost())
        {
            Debug.LogWarning("[LobbyController] StartGame fallback 발생: non-host tried to start.");
            SetStatus("Only host can start.");
            return;
        }

        _startGameInFlight = true;
        SetInteractable(false);
        SetStatus("Starting game (Relay)...");

        try
        {
            await HostStartGameAsync(_cts.Token);
            SetStatus("Game starting...");
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("[LobbyController] StartGame fallback 발생: canceled/timeout.");
            SetStatus("Start canceled/timeout.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LobbyController] StartGame fallback 발생: {ex}");
            SetStatus("Start failed. Check console.");
        }
        finally
        {
            _startGameInFlight = false;
            SetInteractable(true);
            RefreshUI();
        }
    }

    private async Task HostStartGameAsync(CancellationToken externalCt)
    {
        if (_lobby == null)
            throw new InvalidOperationException("Lobby is null.");

        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[LobbyController] HostStartGame fallback 발생: NetworkManager.Singleton is null.");
            throw new InvalidOperationException("NetworkManager missing");
        }

        var utp = nm.GetComponent<UnityTransport>();
        if (utp == null)
        {
            Debug.LogWarning("[LobbyController] HostStartGame fallback 발생: UnityTransport missing.");
            throw new InvalidOperationException("UnityTransport missing");
        }

        // Host만 호출해야 함
        if (!IsHost())
        {
            Debug.LogWarning("[LobbyController] HostStartGame fallback 발생: called by non-host.");
            throw new InvalidOperationException("Only host can start game");
        }

        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _updateTimeoutSeconds);

        // 0) NGO가 이미 떠있으면 Relay 세팅 적용 위해 재시작
        if (nm.IsListening)
        {
            Debug.LogWarning("[LobbyController] HostStartGame fallback 발생: NetworkManager already listening. Shutdown and restart with Relay.");
            nm.Shutdown();
            await Task.Yield();
        }

        // 1) Relay allocation: maxConnections는 "클라이언트 수"(호스트 제외)
        int maxPlayers = _lobby.MaxPlayers > 0 ? _lobby.MaxPlayers : 4;
        int maxConnections = Mathf.Max(0, maxPlayers - 1);
        if (maxConnections <= 0)
            Debug.LogWarning("[LobbyController] Relay fallback 발생: maxConnections <= 0 (maxPlayers check).");

        // 2) Relay 생성 + JoinCode
        SetStatus("Creating Relay allocation...");
        Task<Allocation> allocReq = RelayService.Instance.CreateAllocationAsync(maxConnections);
        Allocation alloc = await WaitWithCancellation(allocReq, timeoutCts.Token);

        SetStatus("Getting Relay join code...");
        Task<string> codeReq = RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
        string joinCode = await WaitWithCancellation(codeReq, timeoutCts.Token);

        // 3) Lobby Data 업데이트 + Lock
        SetStatus("Updating lobby data...");
        await SetLobbyStartedAsync(joinCode, timeoutCts.Token);

        // 4) Relay 세팅(Transport 2.0 문서 기준 생성자)
        // dtls: secured UDP. (WebGL이면 wss로 바꿔야 함)
        string connectionType = "dtls";
        RelayServerData rsd = BuildRelayServerDataFromAllocation(alloc, connectionType);
        utp.SetRelayServerData(rsd);

        // 5) Host 시작
        SetStatus("Starting Host (NGO + Relay)...");
        bool ok = nm.StartHost();
        if (!ok)
        {
            Debug.LogWarning("[LobbyController] StartHost fallback 발생: StartHost returned false.");
            throw new InvalidOperationException("StartHost failed");
        }

        // 6) 네트워크 씬 로드 (전원 동기)
        SetStatus("Loading network scene...");
        nm.SceneManager.LoadScene(_gameSceneName, LoadSceneMode.Single);
    }

    private async Task SetLobbyStartedAsync(string joinCode, CancellationToken externalCt)
    {
        if (_lobby == null)
            throw new InvalidOperationException("Lobby null");

        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _updateTimeoutSeconds);

        try
        {
            var merged = new Dictionary<string, DataObject>();

            if (_lobby.Data != null)
            {
                foreach (var kv in _lobby.Data)
                    merged[kv.Key] = kv.Value;
            }

            // merged[_relayJoinCodeKey] = new DataObject(DataObject.VisibilityOptions.Member, joinCode);
            // merged[_gameStartedKey] = new DataObject(DataObject.VisibilityOptions.Member, "1");
            // merged[_gameSceneKey] = new DataObject(DataObject.VisibilityOptions.Member, _gameSceneName);

            merged[_relayJoinCodeKey] = new DataObject(DataObject.VisibilityOptions.Public, joinCode);
            merged[_gameStartedKey] = new DataObject(DataObject.VisibilityOptions.Public, "1");
            merged[_gameSceneKey] = new DataObject(DataObject.VisibilityOptions.Public, _gameSceneName);

            Debug.Log("relayJoinCodeKey: " + joinCode);

            var options = new UpdateLobbyOptions
            {
                Data = merged,
                IsLocked = true
            };

            Task<Lobby> req = LobbyService.Instance.UpdateLobbyAsync(_lobby.Id, options);
            _lobby = await WaitWithCancellation(req, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is LobbyServiceException || ex is OperationCanceledException)
        {
            Debug.LogWarning($"[LobbyController] LobbyUpdate fallback 발생: {ex.Message}");
            throw;
        }
    }

    private async void OnClickLeave()
    {
        CancelRunningOps();

        if (_lobby == null)
        {
            Debug.LogWarning("[LobbyController] Leave fallback 발생: lobby is null. Clearing local context and returning to MainMenu.");
            ClearClientLobbyContext();
            RefreshUI();
            SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
        }

        SetInteractable(false);
        SetStatus("Leaving lobby...");

        try
        {
            // Host가 나갈 때: 남은 플레이어가 있으면 HostId를 넘기고 나간다. (Host migration)
            // 공식 문서: UpdateLobby로 HostId 변경 가능.
            if (IsHost())
            {
                string nextHostId = PickNextHostId();
                if (!string.IsNullOrEmpty(nextHostId))
                {
                    await TransferHostAsync(nextHostId, _cts.Token);
                    SetStatus($"Host transferred to {Short(nextHostId)}. Leaving...");
                }
            }

            // 자기 자신 제거
            await RemoveSelfAsync(_cts.Token);

            SetStatus("Left lobby.");
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("[LobbyController] Leave fallback 발생: canceled/timeout. Clearing local context anyway.");
            SetStatus("Leave timeout. Returning...");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LobbyController] Leave fallback 발생: {ex}\nClearing local context anyway.");
            SetStatus("Leave failed. Returning...");
        }
        finally
        {
            // 핵심: 서버 호출이 실패해도 로컬 컨텍스트는 무조건 제거
            ClearClientLobbyContext();

            // UI 반영 후 이동
            RefreshUI();
            SceneManager.LoadScene(_mainMenuSceneName, LoadSceneMode.Single);
        }
    }

    private void CancelRunningOps()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }

    private void ClearClientLobbyContext()
    {
        _lobby = null;
        _pollInFlight = false;
        _hbT = 0f;

        _gameStartObserved = false;

        PlayerPrefs.DeleteKey(PrefKeyLobbyId);
        PlayerPrefs.Save();

        QuickSessionContext.Instance.ClearLobbyContext();
    }

    private bool IsHost()
    {
        if (_lobby == null || string.IsNullOrEmpty(_playerId)) return false;
        return _lobby.HostId == _playerId;
    }

    private string PickNextHostId()
    {
        if (_lobby == null) return null;

        // 자기 자신 제외, 남아있는 첫 번째 플레이어에게 넘긴다.
        var p = _lobby.Players?.FirstOrDefault(x => x != null && x.Id != _playerId);
        return p?.Id;
    }

    private async Task TransferHostAsync(string newHostId, CancellationToken externalCt)
    {
        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _updateTimeoutSeconds);

        try
        {
            var options = new UpdateLobbyOptions
            {
                HostId = newHostId
            };

            Task<Lobby> req = LobbyService.Instance.UpdateLobbyAsync(_lobby.Id, options);
            _lobby = await WaitWithCancellation(req, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is LobbyServiceException || ex is OperationCanceledException)
        {
            Debug.LogWarning($"[LobbyController] HostMigration fallback 발생: {ex.Message}");
            throw;
        }
    }

    private async Task RemoveSelfAsync(CancellationToken externalCt)
    {
        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _leaveTimeoutSeconds);

        try
        {
            Task req = LobbyService.Instance.RemovePlayerAsync(_lobby.Id, _playerId);
            await WaitWithCancellation(req, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is LobbyServiceException || ex is OperationCanceledException)
        {
            Debug.LogWarning($"[LobbyController] RemovePlayer fallback 발생: {ex.Message}");
            throw;
        }
    }

    private async Task PollLobbyAsync(CancellationToken externalCt)
    {
        if (_lobby == null) return;

        _pollInFlight = true;

        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _pollTimeoutSeconds);

        try
        {
            Task<Lobby> req = LobbyService.Instance.GetLobbyAsync(_lobby.Id);
            _lobby = await WaitWithCancellation(req, timeoutCts.Token);

            // 성공 시: 백오프 리셋 + 정상 주기
            _pollBackoffSeconds = 0f;
            _nextPollAt = Time.unscaledTime + _pollIntervalSeconds;

            // started 감지: 플래그만 세팅 (pollInterval 내리지 말 것)
            if (!IsHost())
            {
                if (TryGetLobbyData(_gameStartedKey, out string started) && started == "1")
                    _gameStartObserved = true;
            }

            RefreshUI();
        }
        catch (LobbyServiceException e)
        {
            // 429 처리: 쿨다운 + 백오프
            if (IsTooManyRequests(e))
            {
                ApplyPollBackoff();
                Debug.LogWarning($"[LobbyController] Poll fallback 발생: Too Many Requests. Backoff={_pollBackoffSeconds:0.0}s");
                return;
            }

            Debug.LogWarning($"[LobbyController] Poll fallback 발생: {e.Message}");
            SetStatus("Lobby closed or you were removed.");
            ClearSavedLobby();
            _lobby = null;
            RefreshUI();
        }
        catch (OperationCanceledException)
        {
            // 타임아웃도 백오프 줘라. (요청이 밀린 상태일 가능성 큼)
            ApplyPollBackoff();
            Debug.LogWarning($"[LobbyController] Poll fallback 발생: timeout/canceled. Backoff={_pollBackoffSeconds:0.0}s");
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private void ApplyPollBackoff()
    {
        _pollBackoffSeconds = Mathf.Clamp(
            _pollBackoffSeconds <= 0f ? PollBackoffMin : _pollBackoffSeconds * 2f,
            PollBackoffMin,
            PollBackoffMax
        );
        _nextPollAt = Time.unscaledTime + _pollBackoffSeconds;
    }

    private static bool IsTooManyRequests(LobbyServiceException e)
    {
        // Unity 쪽은 StatusCode가 있는 버전/없는 버전이 섞여있음.
        // 둘 다 커버.
        try
        {
            // 일부 버전: e.Reason / e.ErrorCode / e.StatusCode 있음
            var prop = e.GetType().GetProperty("StatusCode");
            if (prop != null)
            {
                object v = prop.GetValue(e);
                if (v is long l && l == 429) return true;
                if (v is int i && i == 429) return true;
            }
        }
        catch { }

        return e.Message != null && e.Message.IndexOf("Too Many Requests", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private async Task ClientTryJoinGameIfStartedAsync(CancellationToken externalCt)
    {
        if (_clientJoinInFlight) return;
        if (_lobby == null) return;

        // Join 스팸 방지
        if (Time.unscaledTime < _nextJoinAttemptAt)
            return;

        // Poll에서 갱신된 _lobby.Data만 사용 (여기서 GetLobbyAsync 절대 호출하지 말 것)
        if (!TryGetLobbyData(_gameStartedKey, out string started) || started != "1")
            return;

        if (!TryGetLobbyData(_relayJoinCodeKey, out string joinCode) || string.IsNullOrWhiteSpace(joinCode))
        {
            Debug.LogWarning("[LobbyController] ClientJoin fallback 발생: relayJoinCode missing/empty while gameStarted=1.");
            return;
        }

        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[LobbyController] ClientJoin fallback 발생: NetworkManager.Singleton is null.");
            return;
        }

        var utp = nm.GetComponent<UnityTransport>();
        if (utp == null)
        {
            Debug.LogWarning("[LobbyController] ClientJoin fallback 발생: UnityTransport missing.");
            return;
        }

        _clientJoinInFlight = true;

        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _joinTimeoutSeconds);

        try
        {
            SetStatus("Joining game (Relay)...");

            if (nm.IsListening)
            {
                Debug.LogWarning("[LobbyController] ClientJoin fallback 발생: NetworkManager already listening. Shutdown and restart with Relay.");
                nm.Shutdown();
                await Task.Yield();
            }

            Task<JoinAllocation> joinReq = RelayService.Instance.JoinAllocationAsync(joinCode);
            JoinAllocation joinAlloc = await WaitWithCancellation(joinReq, timeoutCts.Token);

            string connectionType = "dtls";
            RelayServerData rsd = BuildRelayServerDataFromJoinAllocation(joinAlloc, connectionType);
            utp.SetRelayServerData(rsd);

            bool ok = nm.StartClient();
            if (!ok)
            {
                Debug.LogWarning("[LobbyController] ClientJoin fallback 발생: StartClient returned false.");
                SetStatus("Client start failed.");
                ScheduleJoinRetry(backoff: true);
                return;
            }

            await WaitForClientConnectedAsync(nm, timeoutCts.Token);

            // 성공하면 백오프 리셋
            _joinBackoffSeconds = 0f;
            _gameStartObserved = false;

            SetStatus("Client connected. Waiting for scene load...");
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("[LobbyController] ClientJoin fallback 발생: timeout/canceled.");
            SetStatus("Join timeout.");
            ScheduleJoinRetry(backoff: true);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LobbyController] ClientJoin fallback 발생: {ex}");
            SetStatus("Join failed. Check console.");
            ScheduleJoinRetry(backoff: true);
        }
        finally
        {
            _clientJoinInFlight = false;
        }
    }

    private void ScheduleJoinRetry(bool backoff)
    {
        if (!backoff)
        {
            _nextJoinAttemptAt = Time.unscaledTime + JoinBackoffMin;
            return;
        }

        _joinBackoffSeconds = Mathf.Clamp(
            _joinBackoffSeconds <= 0f ? JoinBackoffMin : _joinBackoffSeconds * 2f,
            JoinBackoffMin,
            JoinBackoffMax
        );

        _nextJoinAttemptAt = Time.unscaledTime + _joinBackoffSeconds;
    }

    private static async Task WaitForClientConnectedAsync(NetworkManager nm, CancellationToken ct)
    {
        // Host의 NetworkSceneManager 로드를 따라가려면 우선 연결이 되어야 함.
        // 짧게 폴링해서 ConnectedClient가 되는지 확인.
        const int maxSteps = 50; // 대략 50 프레임
        for (int i = 0; i < maxSteps; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (nm != null && nm.IsClient && nm.IsConnectedClient)
                return;

            await Task.Yield();
        }

        Debug.LogWarning("[LobbyController] ClientJoin fallback 발생: client not connected yet (still waiting).");
    }

    private bool TryGetLobbyData(string key, out string value)
    {
        value = null;
        if (_lobby?.Data == null) return false;
        if (!_lobby.Data.TryGetValue(key, out DataObject obj)) return false;
        value = obj?.Value;
        return true;
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

    private async Task HeartbeatAsync(CancellationToken externalCt)
    {
        if (_lobby == null) return;
        if (!IsHost()) return;

        // Lobby host는 주기적으로 heartbeat를 보내야 만료를 막는다.
        try
        {
            await LobbyService.Instance.SendHeartbeatPingAsync(_lobby.Id);
        }
        catch (LobbyServiceException e)
        {
            Debug.LogWarning($"[LobbyController] Heartbeat fallback 발생: {e.Message}");
        }
    }

    private void RefreshUI()
    {
        // Lobby info
        if (_lobbyNameText != null)
            _lobbyNameText.text = _lobby != null ? _lobby.Name : "-";

        if (_lobbyCodeText != null)
            _lobbyCodeText.text = _lobby != null ? _lobby.LobbyCode : "-";

        // Slots
        for (int i = 0; i < 4; i++)
        {
            if (_slotNameTexts[i] != null)
                _slotNameTexts[i].text = "-";

            if (_slotHostIcons[i] != null)
                _slotHostIcons[i].gameObject.SetActive(false);
        }

        if (_lobby == null)
        {
            if (_startGameButton != null) _startGameButton.interactable = false;
            return;
        }

        int count = _lobby.Players != null ? _lobby.Players.Count : 0;
        for (int i = 0; i < Mathf.Min(4, count); i++)
        {
            var p = _lobby.Players[i];
            string name = ExtractPlayerName(p);
            if (_slotNameTexts[i] != null)
                _slotNameTexts[i].text = string.IsNullOrEmpty(name) ? $"Player({Short(p.Id)})" : name;

            if (_slotHostIcons[i] != null)
                _slotHostIcons[i].gameObject.SetActive(p.Id == _lobby.HostId);
        }

        // Status
        string role = IsHost() ? "Host" : "Client";
        bool locked = _lobby.IsLocked;
        int max = _lobby.MaxPlayers;

        // Status는 "진행 중 작업"이 있을 때 덮어쓰지 않는다.
        if (!_clientJoinInFlight && !_startGameInFlight)
        {
            SetStatus($"{role} | Players {count}/{max} | Locked={locked}");
        }

        // Buttons
        if (_startGameButton != null)
            _startGameButton.interactable = IsHost();
    }

    private string ExtractPlayerName(Player p)
    {
        if (p == null || p.Data == null) return null;

        if (p.Data.TryGetValue("name", out PlayerDataObject dataObj))
        {
            return dataObj != null ? dataObj.Value : null;
        }

        return null;
    }

    private void SetInteractable(bool value)
    {
        if (_startGameButton != null) _startGameButton.interactable = value && IsHost();
        if (_leaveButton != null) _leaveButton.interactable = value;
    }

    private void SetStatus(string msg)
    {
        if (_statusText != null)
            _statusText.text = msg;
    }

    private void SaveLobbyId(string lobbyId)
    {
        if (string.IsNullOrWhiteSpace(lobbyId)) return;
        PlayerPrefs.SetString(PrefKeyLobbyId, lobbyId);
        PlayerPrefs.Save();
    }

    private void ClearSavedLobby()
    {
        PlayerPrefs.DeleteKey(PrefKeyLobbyId);
        PlayerPrefs.Save();
    }

    private async Task EnsureSignedInAsync(CancellationToken ct)
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            await UnityServices.InitializeAsync();
        }

        ct.ThrowIfCancellationRequested();

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
    }

    private async Task<Lobby> ReconnectOrJoinByIdAsync(string lobbyId, CancellationToken externalCt)
    {
        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _reconnectTimeoutSeconds);

        // 1) Join 먼저
        try
        {
            var opt = new JoinLobbyByIdOptions { Player = BuildPlayer(_playerName) };
            Task<Lobby> joinReq = LobbyService.Instance.JoinLobbyByIdAsync(lobbyId, opt);
            return await WaitWithCancellation(joinReq, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is LobbyServiceException || ex is OperationCanceledException)
        {
            Debug.LogWarning($"[LobbyController] JoinLobbyById fallback 발생: {ex.Message} (trying GetLobby)");
        }

        // 2) Join 실패면 GetLobby로 “이미 멤버” 케이스 확인
        try
        {
            Task<Lobby> getReq = LobbyService.Instance.GetLobbyAsync(lobbyId);
            Lobby got = await WaitWithCancellation(getReq, timeoutCts.Token);

            if (got.Players != null && got.Players.Any(x => x != null && x.Id == _playerId))
                return got;

            Debug.LogWarning("[LobbyController] Reconnect fallback 발생: GetLobby succeeded but I'm not a member.");
            return null;
        }
        catch (Exception ex) when (ex is LobbyServiceException || ex is OperationCanceledException)
        {
            Debug.LogWarning($"[LobbyController] GetLobby reconnect fallback 발생: {ex.Message}");
            return null;
        }
    }

    private Player BuildPlayer(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            playerName = $"Player_{UnityEngine.Random.Range(0, 10000):0000}";
            Debug.LogWarning($"[LobbyController] PlayerName fallback 발생: empty cached name. Generated={playerName}");
        }

        return new Player(
            id: AuthenticationService.Instance.PlayerId,
            data: new System.Collections.Generic.Dictionary<string, PlayerDataObject>
            {
                { "name", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, playerName) }
            }
        );
    }

    private static CancellationTokenSource CreateLinkedTimeoutCts(CancellationToken externalCt, float timeoutSeconds)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        if (timeoutSeconds > 0f)
        {
            int ms = Mathf.Clamp((int)(timeoutSeconds * 1000f), 1, int.MaxValue);
            linked.CancelAfter(ms);
        }
        else
        {
            Debug.LogWarning("[LobbyController] Timeout fallback 발생: timeoutSeconds <= 0. No timeout will be applied.");
        }

        return linked;
    }

    private static async Task<T> WaitWithCancellation<T>(Task<T> task, CancellationToken ct)
    {
        Task cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        Task finished = await Task.WhenAny(task, cancelTask);
        if (finished == cancelTask)
            throw new OperationCanceledException(ct);

        return await task;
    }

    private static async Task WaitWithCancellation(Task task, CancellationToken ct)
    {
        Task cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        Task finished = await Task.WhenAny(task, cancelTask);
        if (finished == cancelTask)
            throw new OperationCanceledException(ct);

        await task;
    }

    private static string Short(string id)
    {
        if (string.IsNullOrEmpty(id)) return "-";
        return id.Length <= 6 ? id : id.Substring(0, 6);
    }
}
