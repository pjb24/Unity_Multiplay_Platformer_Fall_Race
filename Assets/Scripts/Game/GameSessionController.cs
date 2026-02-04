using System;
using Unity.Netcode;
using UnityEngine;
using System.Collections;

public enum E_GameSessionState : byte
{
    Lobby = 0,
    Countdown = 1,
    Running = 2,
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
    public static GameSessionController Instance { get; private set; }

    public E_GameSessionState State => _state.Value;
    public double StartAtServerTime => _startAtServerTime.Value;
    public NetworkList<ReadyEntry> ReadyList => _readyList; // 읽기 전용으로 쓰고, 수정은 Server만

    private readonly NetworkVariable<E_GameSessionState> _state =
        new NetworkVariable<E_GameSessionState>(E_GameSessionState.Lobby);

    private readonly NetworkVariable<double> _startAtServerTime =
        new NetworkVariable<double>(0.0);

    private NetworkList<ReadyEntry> _readyList = new NetworkList<ReadyEntry>();

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
        if (!IsServer) return;

        // Game 씬 진입 시점에 이미 연결이 올라온 상태라는 전제
        BuildReadyListFromConnectedClients();

        // 씬 전환 직후 타이밍 보정: 다음 프레임에 한 번 더 동기화
        StartCoroutine(RebuildNextFrame());

        _state.Value = E_GameSessionState.Lobby;
        _startAtServerTime.Value = 0.0;

        NetworkManager.OnClientConnectedCallback += OnClientConnected_Server;
        NetworkManager.OnClientDisconnectCallback += OnClientDisconnected_Server;
    }

    private IEnumerator RebuildNextFrame()
    {
        yield return null;
        BuildReadyListFromConnectedClients();
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;

        if (NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnected_Server;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected_Server;
        }
    }

    private void Update()
    {
        if (!IsServer) return;

        // Countdown이 시작되면 서버도 Running으로 전환 (권장)
        if (_state.Value == E_GameSessionState.Countdown)
        {
            double now = NetworkManager.ServerTime.Time;
            if (now >= _startAtServerTime.Value && _startAtServerTime.Value > 0.0)
            {
                _state.Value = E_GameSessionState.Running;
            }
        }
    }

    // ----------------------------
    // Ready: Everyone can invoke -> server validates sender
    // Client만 토글 가능
    // ----------------------------
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

        Debug.Log($"sender: {sender}, Ready: {e.isReady}");
    }

    // ----------------------------
    // Start: Everyone can invoke, but only Host accepted
    // Host only, all clients ready required
    // ----------------------------
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

        _startAtServerTime.Value = NetworkManager.ServerTime.Time + 3.0;
        _state.Value = E_GameSessionState.Countdown;
    }

    // -------------------------------------------------
    // Helpers
    // -------------------------------------------------
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

    private int FindReadyIndex(ulong clientId)
    {
        if (_readyList == null) return -1;
        for (int i = 0; i < _readyList.Count; i++)
        {
            if (_readyList[i].clientId == clientId) return i;
        }
        return -1;
    }

    // ----------------------------
    // Server list maintenance
    // ----------------------------
    private void BuildReadyListFromConnectedClients()
    {
        _readyList.Clear();

        ulong hostId = NetworkManager.ServerClientId;

        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            bool ready = (id == hostId); // Host는 항상 Ready
            _readyList.Add(new ReadyEntry(id, ready));
        }
    }

    private void OnClientConnected_Server(ulong clientId)
    {
        // Running/Countdown 중 합류 정책은 지금은 허용하지 않는 걸 추천.
        // 일단 Lobby일 때만 반영.
        if (_state.Value != E_GameSessionState.Lobby) return;

        if (FindReadyIndex(clientId) >= 0) return;

        bool ready = (clientId == NetworkManager.ServerClientId); // 혹시 몰라서
        _readyList.Add(new ReadyEntry(clientId, ready));
    }

    private void OnClientDisconnected_Server(ulong clientId)
    {
        int idx = FindReadyIndex(clientId);
        if (idx < 0) return;

        _readyList.RemoveAt(idx);
    }
}
