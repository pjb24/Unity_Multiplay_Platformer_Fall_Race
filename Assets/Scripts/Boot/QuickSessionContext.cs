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
    public static QuickSessionContext Instance => _instance;

    public enum E_RelayProtocol
    {
        DTLS = 0,
        UDP = 1,
    }

    [Header("Config")]
    [SerializeField] private int _maxPlayers = 4;
    public int MaxPlayers => _maxPlayers;
    [SerializeField] private float _lobbyPollIntervalSec = 4f;     // 권장 3~5
    [SerializeField] private float _heartbeatIntervalSec = 15f;    // 권장 12~15 (Host만)

    [SerializeField] private E_RelayProtocol _relayProtocol = E_RelayProtocol.DTLS;

    [SerializeField] private string _gameSceneName = "Game";

    private static QuickSessionContext _instance;

    private Lobby _joinedLobby;
    private CancellationTokenSource _pollCts;
    private CancellationTokenSource _heartbeatCts;

    // Lobby Data Keys
    private const string K_RelayJoinCode = "relayJoinCode";
    private const string K_HostPlayerId = "relayHost";

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

    public async Task<SessionResult> TryJoinAsClientThenEnterGameAsync(string username)
    {
        var res = await JoinLobbyAsClientAsync(username);
        if (!res.ok)
            Debug.LogWarning($"[QuickSession] fallback 발생: TryJoinAsClient failed: {res.failCode} / {res.message}");
        return res;
    }

    public async Task<SessionResult> TryStartAsHostThenEnterGameAsync(string username)
    {
        var res = await CreateLobbyAsHostAsync(username);
        if (!res.ok)
            Debug.LogWarning($"[QuickSession] fallback 발생: TryStartAsHost failed: {res.failCode} / {res.message}");
        return res;
    }

    // ----------------------------
    // Host Flow
    // ----------------------------
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
            // 1) Relay Allocation (host 제외 연결 수 = maxPlayers - 1)
            int maxConnections = Mathf.Max(0, _maxPlayers - 1);
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            string relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            // 2) Lobby Create + Data 저장 (relayJoinCode)
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

            // 3) Relay 정보로 UnityTransport의 네트워크 경로 구성
            // DTLS 사용(기본 권장)
            string protocol = GetRelayProtocolString();
            transport.SetRelayServerData(BuildRelayServerDataFromAllocation(allocation, protocol));

            // 2) 서버 시작 확인 후 씬 전환 (한 프레임 미뤄도 됨)
            nm.OnServerStarted += OnServerStarted;

            // 4) NGO StartHost
            if (!nm.StartHost())
            {
                Debug.LogWarning("[QuickSession] Host fallback 발생: NetworkManager.StartHost failed.");
                return SessionResult.Fail("start_host_failed", "StartHost failed");
            }

            // 5) Host만 Heartbeat + Poll 시작
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

    void OnServerStarted()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[QuickSession] OnServerStarted fallback 발생: NetworkManager.Singleton is null.");
            return;
        }

        nm.OnServerStarted -= OnServerStarted;

        // 3) Host가 네트워크 씬 로드 지휘
        nm.SceneManager.LoadScene(_gameSceneName, LoadSceneMode.Single);
    }

    // ----------------------------
    // Client Flow (Quick Join)
    // ----------------------------
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
            // 1) Quick Join Lobby
            var joinOptions = new QuickJoinLobbyOptions
            {
                Player = new Player(AuthenticationService.Instance.PlayerId, data: new Dictionary<string, PlayerDataObject>
                {
                    { "name", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, username) }
                })
            };

            _joinedLobby = await LobbyService.Instance.QuickJoinLobbyAsync(joinOptions);

            // 2) Lobby Data에서 relayJoinCode 확인
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

            // 3) Relay Join
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);

            // 4) Relay 정보로 UnityTransport의 네트워크 경로 구성
            string protocol = GetRelayProtocolString();
            transport.SetRelayServerData(BuildRelayServerDataFromJoinAllocation(joinAllocation, protocol));

            nm.OnClientConnectedCallback += OnClientConnected;

            // 5) NGO StartClient
            if (!nm.StartClient())
            {
                Debug.LogWarning("[QuickSession] Client fallback 발생: NetworkManager.StartClient failed.");
                return SessionResult.Fail("start_client_failed", "StartClient failed");
            }

            // 6) Poll 시작(클라에서도 lobby 상태 변화 감시용)
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

    void OnClientConnected(ulong clientId)
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

    // ----------------------------
    // Heartbeat / Poll (no UI refs)
    // ----------------------------
    private void StartHeartbeat(string lobbyId)
    {
        StopHeartbeat();
        _heartbeatCts = new CancellationTokenSource();
        _ = HeartbeatLoopAsync(lobbyId, _heartbeatCts.Token);
    }

    private void StopHeartbeat()
    {
        if (_heartbeatCts == null) return;
        _heartbeatCts.Cancel();
        _heartbeatCts.Dispose();
        _heartbeatCts = null;
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
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(lobbyId, _pollCts.Token);
    }

    private void StopPolling()
    {
        if (_pollCts == null) return;
        _pollCts.Cancel();
        _pollCts.Dispose();
        _pollCts = null;
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

    private bool EnsureAuthReady()
    {
        // Bootstrap에서 이미 Initialize + SignIn이 끝났다는 전제
        return AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn;
    }
}
