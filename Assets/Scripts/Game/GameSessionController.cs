using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public enum E_GameSessionState : byte
{
    Lobby = 0,
    Countdown = 1,
    Running = 2,
    Result = 3,
}

public struct ReadyEntry : INetworkSerializable, IEquatable<ReadyEntry>
{
    public ulong clientId;
    public bool isReady;

    public ReadyEntry(ulong clientId, bool isReady)
    {
        this.clientId = clientId;
        this.isReady = isReady;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref clientId);
        serializer.SerializeValue(ref isReady);
    }

    public bool Equals(ReadyEntry other)
        => clientId == other.clientId && isReady == other.isReady;

    public override bool Equals(object obj)
        => obj is ReadyEntry other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(clientId, isReady);
}

public sealed class GameSessionController : NetworkBehaviour
{
    // ============================
    // Singleton / Public API
    // ============================
    public static GameSessionController Instance { get; private set; }

    public E_GameSessionState State => _state.Value;
    public double StartAtServerTime => _startAtServerTime.Value;

    // 읽기 전용으로 쓰고, 수정은 Server만
    public NetworkList<ReadyEntry> ReadyList => _readyList;

    // ============================
    // Local Event Relay (NOT exposed as event)
    // ============================
    private Action<E_GameSessionState, E_GameSessionState> _onStateChanged;

    public void AddStateListener(Action<E_GameSessionState, E_GameSessionState> listener)
    {
        if (listener == null)
        {
            Debug.LogWarning("[GameSession] AddStateListener fallback 발생: listener is null.");
            return;
        }

        _onStateChanged -= listener; // 중복 방지
        _onStateChanged += listener;
    }

    public void RemoveStateListener(Action<E_GameSessionState, E_GameSessionState> listener)
    {
        if (listener == null)
        {
            Debug.LogWarning("[GameSession] RemoveStateListener fallback 발생: listener is null.");
            return;
        }

        _onStateChanged -= listener;
    }

    // ============================
    // Tunables
    // ============================
    [Header("Timings (seconds)")]
    [SerializeField] private double _countdownSeconds = 3.0;
    [SerializeField] private double _resultSeconds = 2.0;

    // ============================
    // Network State
    // ============================
    private readonly NetworkVariable<E_GameSessionState> _state =
        new NetworkVariable<E_GameSessionState>(E_GameSessionState.Lobby);

    // Countdown 목표 시각 (ServerTime)
    private readonly NetworkVariable<double> _startAtServerTime =
        new NetworkVariable<double>(0.0);

    // Result 종료 시각 (ServerTime)
    private readonly NetworkVariable<double> _resultEndAtServerTime =
        new NetworkVariable<double>(0.0);

    private NetworkList<ReadyEntry> _readyList = new NetworkList<ReadyEntry>();

    // Countdown 진입 시 LockLobby 트리거 1회 보장 플래그
    private bool _lobbyLockRequested;

    // 구독 중복/누락 방지용
    private bool _isStateHooked;

    // ============================
    // Runtime Player Data (Server Only)
    // ============================
    // "플레이 데이터 정리"용: 실제 게임 시스템(타이머/기록/스폰 등) 붙으면 여기에 확장
    private readonly Dictionary<ulong, PlayerRuntimeData> _playerRuntime = new Dictionary<ulong, PlayerRuntimeData>(8);

    private struct PlayerRuntimeData
    {
        public double connectedAtServerTime;
        public bool wasInSession;
    }

    // ============================
    // Unity / NGO Lifecycle
    // ============================
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[GameSession] fallback 발생: duplicate GameSessionController in scene. Destroy duplicate.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        // ❌ _readyList 생성 금지 (여기서 만들면 동기화가 안 붙는 케이스가 나온다)
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (Instance == this) Instance = null;

        // ❌ _readyList.Dispose() 제거
        // NGO가 NetworkBehaviour lifecycle로 관리한다. 수동 Dispose하면 동기화 이벤트가 끊길 수 있다.
    }

    public override void OnNetworkSpawn()
    {
        // 모든 인스턴스(Host/Client)에서 상태 변경 hook
        HookStateChangedOnce();

        var nm = NetworkManager;
        if (nm == null)
        {
            Debug.LogWarning("[GameSession] OnNetworkSpawn fallback 발생: NetworkManager is null.");
            return;
        }

        // Host 종료 감지(클라 관점): 연결이 끊기면 "세션 종료" 로그를 남긴다.
        nm.OnClientDisconnectCallback += OnClientDisconnected_Any;

        if (!IsServer) return;

        // Lobby 상태 초기 구성
        ResetSessionToLobby_Server("spawn_init");

        // 씬 전환 직후 타이밍 보정: 다음 프레임에 한 번 더 동기화
        StartCoroutine(RebuildNextFrame());

        nm.OnClientConnectedCallback += OnClientConnected_Server;
        // 서버는 위 OnClientDisconnected_Any에서 분기 처리(중복구독 방지)
    }

    public override void OnNetworkDespawn()
    {
        // 모든 인스턴스(Host/Client)에서 해제
        UnhookStateChanged();

        var nm = NetworkManager;
        if (nm != null)
        {
            nm.OnClientDisconnectCallback -= OnClientDisconnected_Any;

            if (IsServer)
            {
                nm.OnClientConnectedCallback -= OnClientConnected_Server;
            }
        }
    }

    private void HookStateChangedOnce()
    {
        if (_isStateHooked) return;
        _isStateHooked = true;

        _state.OnValueChanged += HandleStateChanged_Internal;
    }

    private void UnhookStateChanged()
    {
        if (!_isStateHooked) return;
        _isStateHooked = false;

        _state.OnValueChanged -= HandleStateChanged_Internal;
    }

    private void HandleStateChanged_Internal(E_GameSessionState prev, E_GameSessionState next)
    {
        _onStateChanged?.Invoke(prev, next);
    }

    private void Update()
    {
        if (!IsServer) return;

        double now = NetworkManager.ServerTime.Time;

        // Countdown 종료 -> Running
        if (_state.Value == E_GameSessionState.Countdown)
        {
            if (now >= _startAtServerTime.Value && _startAtServerTime.Value > 0.0)
            {
                _state.Value = E_GameSessionState.Running;
                Debug.Log($"[GameSession] State -> Running. now={now:F3}, startAt={_startAtServerTime.Value:F3}");
            }
            return;
        }

        // Result 종료 -> 다음 Countdown
        if (_state.Value == E_GameSessionState.Result)
        {
            if (now >= _resultEndAtServerTime.Value && _resultEndAtServerTime.Value > 0.0)
            {
                RequestStartNextStageCountdown_Server("result_end");
            }
        }
    }

    private IEnumerator RebuildNextFrame()
    {
        yield return null;

        if (!IsServer) yield break;

        // Lobby에서만 안전 재빌드

        if (_state.Value == E_GameSessionState.Lobby)
        {
            RebuildReadyListForLobby_Server();
            RebuildRuntimeDataForLobby_Server();
        }
    }

    // ============================
    // Public Server Control (called by StageProgress etc.)
    // ============================
    public void RequestEnterResult_Server(string reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[GameSession] Result fallback 발생: RequestEnterResult_Server called on non-server.");
            return;
        }

        if (_state.Value != E_GameSessionState.Running)
        {
            Debug.LogWarning($"[GameSession] Result fallback 발생: invalid state transition. state={_state.Value}, reason={reason}");
            return;
        }

        double now = NetworkManager.ServerTime.Time;

        _startAtServerTime.Value = 0.0; // Countdown 타이머 무효화
        _resultEndAtServerTime.Value = now + Mathf.Max(0f, (float)_resultSeconds);
        _state.Value = E_GameSessionState.Result;

        Debug.Log($"[GameSession] State -> Result. now={now:F3}, endAt={_resultEndAtServerTime.Value:F3}, reason={reason}");
    }

    private void RequestStartNextStageCountdown_Server(string reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[GameSession] Countdown fallback 발생: RequestStartNextStageCountdown_Server called on non-server.");
            return;
        }

        if (_state.Value != E_GameSessionState.Result)
        {
            Debug.LogWarning($"[GameSession] Countdown fallback 발생: invalid state transition. state={_state.Value}, reason={reason}");
            return;
        }

        double now = NetworkManager.ServerTime.Time;

        _resultEndAtServerTime.Value = 0.0;
        _startAtServerTime.Value = now + Mathf.Max(0f, (float)_countdownSeconds);
        _state.Value = E_GameSessionState.Countdown;

        Debug.Log($"[GameSession] State -> Countdown(next stage). now={now:F3}, startAt={_startAtServerTime.Value:F3}, reason={reason}");
    }

    // ============================
    // RPC Entry Points
    // ============================
    // Ready: Everyone can invoke -> server validates sender
    // Client만 토글 가능
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void ToggleReadyRpc(RpcParams rpcParams = default)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[GameSession] Ready fallback 발생: ToggleReadyRpc called on non-server.");
            return;
        }

        if (_state.Value != E_GameSessionState.Lobby)
        {
            Debug.LogWarning("[GameSession] Ready fallback 발생: not in Lobby state.");
            return;
        }

        ulong sender = rpcParams.Receive.SenderClientId;

        // Host는 항상 Ready (토글 금지)
        if (sender == NetworkManager.ServerClientId)
        {
            Debug.LogWarning("[GameSession] Ready fallback 발생: host cannot toggle ready (always ready).");
            return;
        }

        int idx = FindReadyIndex(sender);
        if (idx < 0)
        {
            Debug.LogWarning($"[GameSession] Ready fallback 발생: sender not found. sender={sender}");
            return;
        }

        var e = _readyList[idx];
        e.isReady = !e.isReady;
        _readyList[idx] = e;

        Debug.Log($"[GameSession] sender={sender}, Ready={e.isReady}");
    }

    // Start: Everyone can invoke, but only Host accepted
    // Host only, all clients ready required
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void StartRaceRpc(RpcParams rpcParams = default)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[GameSession] Start fallback 발생: StartRaceRpc called on non-server.");
            return;
        }

        if (_state.Value != E_GameSessionState.Lobby)
        {
            Debug.LogWarning("[GameSession] Start fallback 발생: not in Lobby state.");
            return;
        }

        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != NetworkManager.ServerClientId)
        {
            Debug.LogWarning($"[GameSession] Start fallback 발생: non-host tried start. sender={sender}");
            return;
        }

        if (!IsAllClientsReady())
        {
            Debug.LogWarning("[GameSession] Start fallback 발생: not all clients are ready.");
            return;
        }

        double now = NetworkManager.ServerTime.Time;

        _resultEndAtServerTime.Value = 0.0;

        _startAtServerTime.Value = now + Mathf.Max(0f, (float)_countdownSeconds);
        _state.Value = E_GameSessionState.Countdown;

        Debug.Log($"[GameSession] State -> Countdown. now={now:F3}, startAt={_startAtServerTime.Value:F3}");

        // Countdown 진입 직후: Lobby Lock 트리거 (서버 1회)
        RequestLockLobbyOnce_Server("countdown_enter");
    }

    // ============================
    // Lobby Lock Trigger (Server only, once)
    // ============================
    private void RequestLockLobbyOnce_Server(string reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[GameSession] LockLobby fallback 발생: called on non-server.");
            return;
        }

        if (_lobbyLockRequested) return;
        _lobbyLockRequested = true;

        var qs = QuickSessionContext.Instance;
        if (qs == null)
        {
            Debug.LogWarning("[GameSession] LockLobby fallback 발생: QuickSessionContext.Instance is null.");
            return;
        }

        // fire-and-forget: 서버 루프 블로킹 금지
        _ = qs.LockLobbyAsync(reason);
    }

    // ============================
    // Server Session Reset (Match End)
    // ============================
    public void ResetSessionToLobby_Server(string reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[GameSession] ResetSession fallback 발생: called on non-server.");
            return;
        }

        // 카운트다운/런닝 관련 값 리셋
        _startAtServerTime.Value = 0.0;
        _resultEndAtServerTime.Value = 0.0;

        // 상태를 Lobby로
        _state.Value = E_GameSessionState.Lobby;

        // ReadyList 재빌드: Host=Ready, Client=NotReady
        RebuildReadyListForLobby_Server();

        // 런타임 플레이 데이터 리셋(현재 접속자 기준으로 재구성, 타이머/기록/스폰 등 붙이면 여기서 초기화)
        RebuildRuntimeDataForLobby_Server();

        // 다음 매치에서 다시 LockLobby 트리거 가능하도록 플래그 리셋
        _lobbyLockRequested = false;

        Debug.Log($"[GameSession] ResetSessionToLobby done. reason={reason}, connected={NetworkManager.ConnectedClientsIds.Count}");
    }

    // ============================
    // Server Data Rebuild
    // ============================
    private void RebuildReadyListForLobby_Server()
    {
        if (!IsServer)
        {
            Debug.LogWarning("[GameSession] RebuildReadyList fallback 발생: called on non-server.");
            return;
        }

        _readyList.Clear();

        ulong hostId = NetworkManager.ServerClientId;

        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            // Host만 Ready, Client는 NotReady
            bool ready = (id == hostId);
            _readyList.Add(new ReadyEntry(id, ready));
        }
    }

    private void RebuildRuntimeDataForLobby_Server()
    {
        if (!IsServer)
        {
            Debug.LogWarning("[GameSession] RebuildRuntimeData fallback 발생: called on non-server.");
            return;
        }

        _playerRuntime.Clear();

        double now = NetworkManager.ServerTime.Time;

        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            _playerRuntime[id] = new PlayerRuntimeData
            {
                connectedAtServerTime = now,
                wasInSession = false,
            };
        }
    }

    // ============================
    // Public Helpers (Read Only)
    // ============================
    public bool IsAllClientsReady()
    {
        // Host 제외, Client만 검사
        if (_readyList == null)
            return false;

        for (int i = 0; i < _readyList.Count; i++)
        {
            var e = _readyList[i];

            // Host 제외
            if (e.clientId == NetworkManager.ServerClientId)
                continue;

            // Client 중 한 명이라도 Ready가 아니면 불가
            if (!e.isReady)
                return false;
        }

        // Client가 0명이면 루프를 그대로 통과 → true
        return true;
    }

    public bool TryGetMyReady(ulong clientId, out bool isReady)
    {
        int idx = FindReadyIndex(clientId);
        if (idx < 0)
        {
            Debug.LogWarning($"[ReadyState] TryGetMyReady fallback 발생: clientId={clientId} not found in ready list.");

            isReady = false;
            return false;
        }

        isReady = _readyList[idx].isReady;
        return true;
    }

    // ============================
    // Server List Maintenance
    // ============================
    private void OnClientConnected_Server(ulong clientId)
    {
        // 신규 Client 입장 차단은 ConnectionApproval에서 처리.
        // 여기서는 "Lobby일 때만 리스트 반영" 유지.
        if (_state.Value != E_GameSessionState.Lobby) return;

        if (FindReadyIndex(clientId) < 0)
        {
            bool ready = (clientId == NetworkManager.ServerClientId);
            _readyList.Add(new ReadyEntry(clientId, ready));
        }

        if (!_playerRuntime.ContainsKey(clientId))
        {
            _playerRuntime[clientId] = new PlayerRuntimeData
            {
                connectedAtServerTime = NetworkManager.ServerTime.Time,
                wasInSession = false,
            };
        }
    }

    // 공용 Disconnect 콜백: 서버/클라 분기
    private void OnClientDisconnected_Any(ulong clientId)
    {
        var nm = NetworkManager;
        if (nm == null) return;

        if (IsServer)
        {
            OnClientDisconnected_Server(clientId);
            return;
        }

        // 클라 관점: 내 연결이 끊겼으면(=Host 종료/세션 종료 포함) 로그를 남긴다.
        if (clientId == nm.LocalClientId)
        {
            Debug.LogWarning($"[Session] Disconnected -> session_end. state={_state.Value}");
        }
    }

    private void OnClientDisconnected_Server(ulong clientId)
    {
        // Disconnect 시 ReadyList에서 제거
        int idx = FindReadyIndex(clientId);
        if (idx >= 0)
            _readyList.RemoveAt(idx);

        // 플레이 데이터 정리
        CleanupPlayerData_Server(clientId);

        // 게임은 남은 인원으로 유지 (여기서 상태/씬/세션 종료 같은 건 하지 않는다)
        Debug.Log($"[GameSession] Client left. clientId={clientId}, state={_state.Value}, remaining={NetworkManager.ConnectedClientsIds.Count}");
    }

    private void CleanupPlayerData_Server(ulong clientId)
    {
        // Host가 나가는 케이스는 별도 정책이 필요하지만, 현재 요구사항은 "남은 인원으로 유지"가 우선.
        // 일단 데이터만 제거한다.
        if (_playerRuntime.Remove(clientId))
            return;

        Debug.LogWarning($"[GameSession] Leave cleanup fallback 발생: runtime data not found. clientId={clientId}");
    }

    // ============================
    // Internal Helpers
    // ============================
    private int FindReadyIndex(ulong clientId)
    {
        if (_readyList == null) return -1;
        for (int i = 0; i < _readyList.Count; i++)
        {
            if (_readyList[i].clientId == clientId) return i;
        }
        return -1;
    }
}
