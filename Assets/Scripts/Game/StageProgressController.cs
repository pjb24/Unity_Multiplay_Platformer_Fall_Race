using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

public sealed class StageProgressController : NetworkBehaviour
{
    [SerializeField] private int _stagesToFinish = 3;

    // Server only: clientId -> cleared stages count
    private readonly Dictionary<ulong, int> _clearedCountByClient = new Dictionary<ulong, int>(8);

    // Server only: 중복 완료 방지(스테이지 인덱스 기반)
    private readonly Dictionary<ulong, HashSet<int>> _clearedStageSetByClient = new Dictionary<ulong, HashSet<int>>(8);

    private bool _endTriggered;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        RebuildFromConnectedClients();

        NetworkManager.OnClientConnectedCallback += OnClientConnected_Server;
        NetworkManager.OnClientDisconnectCallback += OnClientDisconnected_Server;
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

    // =========================================
    // Client -> Server: stage complete notify
    // =========================================
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void ReportStageClearedRpc(int stageIndex, RpcParams rpcParams = default)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[StageProgress] ReportStageCleared fallback 발생: called on non-server.");
            return;
        }

        if (_endTriggered)
        {
            Debug.LogWarning("[StageProgress] ReportStageCleared fallback 발생: match already ended.");
            return;
        }

        if (_stagesToFinish <= 0)
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: invalid stagesToFinish={_stagesToFinish}");
            return;
        }

        if (stageIndex < 0)
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: invalid stageIndex={stageIndex}");
            return;
        }

        ulong sender = rpcParams.Receive.SenderClientId;

        if (!NetworkManager.ConnectedClientsIds.Contains(sender))
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: sender not connected. sender={sender}");
            return;
        }

        if (!_clearedStageSetByClient.TryGetValue(sender, out var set))
        {
            set = new HashSet<int>();
            _clearedStageSetByClient[sender] = set;
            _clearedCountByClient[sender] = 0;
        }

        // 중복 완료 방지
        if (!set.Add(stageIndex))
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: duplicate clear ignored. sender={sender}, stageIndex={stageIndex}");
            return;
        }

        int newCount = _clearedCountByClient[sender] + 1;
        _clearedCountByClient[sender] = newCount;

        Debug.Log($"[StageProgress] Stage cleared. sender={sender}, stageIndex={stageIndex}, clearedCount={newCount}/{_stagesToFinish}");

        // 완료 도달
        if (newCount >= _stagesToFinish)
        {
            Debug.Log($"[StageProgress] Player finished all stages. sender={sender}");
        }

        // 전원 완료 체크
        if (IsAllConnectedPlayersFinished_Server())
        {
            EndMatch_Server("all_players_finished");
        }
    }

    // =========================================
    // Server: End match trigger
    // =========================================
    private void EndMatch_Server(string reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[StageProgress] EndMatch fallback 발생: called on non-server.");
            return;
        }

        if (_endTriggered) return;
        _endTriggered = true;

        Debug.Log($"[StageProgress] EndMatch triggered. reason={reason}");

        // GameSessionController에 종료 처리가 붙을 예정이면 여기서 호출
        // (현재 요구사항: EndMatch_Server() 호출 지점만 확보)
        var session = GameSessionController.Instance;
        if (session == null)
        {
            Debug.LogWarning("[StageProgress] EndMatch fallback 발생: GameSessionController.Instance is null.");
            return;
        }

        // 1) 세션 리셋 (서버)
        session.ResetSessionToLobby_Server("match_end");

        // 2) 로비 Unlock (Host만 동작, 내부에서 Host 체크/로그 처리)
        var qs = QuickSessionContext.Instance;
        if (qs == null)
        {
            Debug.LogWarning("[StageProgress] EndMatch fallback 발생: QuickSessionContext.Instance is null.");
            return;
        }

        // fire-and-forget (서버 루프 블로킹 금지)
        _ = qs.UnlockLobbyAsync("match_end");
    }

    // =========================================
    // Server: connected clients tracking
    // =========================================
    private void RebuildFromConnectedClients()
    {
        _clearedCountByClient.Clear();
        _clearedStageSetByClient.Clear();

        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            _clearedCountByClient[id] = 0;
            _clearedStageSetByClient[id] = new HashSet<int>();
        }

        _endTriggered = false;

        Debug.Log($"[StageProgress] RebuildFromConnectedClients. connected={NetworkManager.ConnectedClientsIds.Count}");
    }

    private void OnClientConnected_Server(ulong clientId)
    {
        // 게임 중 입장은 ConnectionApproval에서 차단하는 전제
        if (_clearedCountByClient.ContainsKey(clientId)) return;

        _clearedCountByClient[clientId] = 0;
        _clearedStageSetByClient[clientId] = new HashSet<int>();

        Debug.Log($"[StageProgress] Client connected tracked. clientId={clientId}");
    }

    private void OnClientDisconnected_Server(ulong clientId)
    {
        if (_clearedCountByClient.Remove(clientId))
        {
            _clearedStageSetByClient.Remove(clientId);
            Debug.Log($"[StageProgress] Client disconnected removed. clientId={clientId}");
        }

        // 남은 인원으로 유지: 여기서 EndMatch를 강제하지 않는다.
    }

    private bool IsAllConnectedPlayersFinished_Server()
    {
        int required = _stagesToFinish;

        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            if (!_clearedCountByClient.TryGetValue(id, out int count))
            {
                Debug.LogWarning($"[StageProgress] FinishCheck fallback 발생: missing entry. clientId={id}");
                return false;
            }

            if (count < required)
                return false;
        }

        return true;
    }
}
