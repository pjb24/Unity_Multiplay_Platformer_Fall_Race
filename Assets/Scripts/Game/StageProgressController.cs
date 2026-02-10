using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

public sealed class StageProgressController : NetworkBehaviour
{
    [SerializeField] private int _stagesToFinish = 3;
    [SerializeField] private StageSpawnPoints _spawnPoints;
    [SerializeField] private bool _warpOnCountdown = true;
    [SerializeField] private int _stageCheckpointIndexBase = 1000;
    [SerializeField] private int _stageCheckpointIndexStride = 1000;

    [Header("Goal Window")]
    [SerializeField] private float _goalWindowSeconds = 60f;

    [Header("Retire Rule")]
    [SerializeField] private float _retirePenaltySeconds = 90f;

    // Server only: clientId -> resolved stages count (Goal 도착 or Retire 처리 완료)
    private readonly Dictionary<ulong, int> _clearedCountByClient = new Dictionary<ulong, int>(8);

    // Server only: 중복 완료 방지(스테이지 인덱스 기반) - "resolved" 의미 (Goal/Retire)
    private readonly Dictionary<ulong, HashSet<int>> _clearedStageSetByClient = new Dictionary<ulong, HashSet<int>>(8);

    private bool _endTriggered;

    // "전원 Resolved -> Result" 중복 진입 방지 (스테이지 인덱스 단위)
    private readonly HashSet<int> _resultRequestedStages = new HashSet<int>();

    // 구독 중복/누락 방지용
    private bool _isSessionHooked;

    // Server only: current stage index
    private int _currentStageIndex;

    // Server only: stageIndex -> first arrival & goal window info
    private readonly Dictionary<int, StageGoalWindowInfo> _goalWindowByStage = new Dictionary<int, StageGoalWindowInfo>();

    // Server only: stageIndex -> (clientId -> arrival info)
    private readonly Dictionary<int, Dictionary<ulong, StageArrivalInfo>> _arrivalByStage =
        new Dictionary<int, Dictionary<ulong, StageArrivalInfo>>();

    // Server only: stageIndex -> run start time (server time)
    private readonly Dictionary<int, double> _stageRunStartTimeByStage = new Dictionary<int, double>();

    private struct StageGoalWindowInfo
    {
        public bool hasFirstArrival;
        public ulong firstArriver;

        // 첫 Goal 도착 시점(서버 시간)
        public double firstGoalServerTime;

        // firstGoalServerTime + goalWindowSeconds
        public double goalWindowEndServerTime;

        // window 만료에 따른 Retire 일괄 적용 완료 여부
        public bool retireResolvedApplied;
    }

    public struct StageArrivalInfo
    {
        // 클라 보고가 들어온 시각(서버 시간) - 타임아웃 Retire의 경우 0
        public double reportServerTime;

        // 기록에 사용될 최종 시간(스테이지 Run 시작부터 경과된 시간)
        // - 정상 도착: reportServerTime - stageRunStartTime
        // - 늦게 도착/Retire: (firstGoalServerTime + retirePenaltySeconds) - stageRunStartTime
        public double finalRecordSecondsFromStageStart;

        public bool withinGoalWindow;
        public bool retired;
    }

    public readonly struct StageResultEntry
    {
        public StageResultEntry(ulong clientId, int rank, StageArrivalInfo arrivalInfo)
        {
            ClientId = clientId;
            Rank = rank;
            ArrivalInfo = arrivalInfo;
        }

        public ulong ClientId { get; }
        public int Rank { get; }
        public StageArrivalInfo ArrivalInfo { get; }
    }

    private void Update()
    {
        if (!IsServer) return;
        ResolveGoalWindowTimeouts_Server();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            RebuildFromConnectedClients();

            NetworkManager.OnClientConnectedCallback += OnClientConnected_Server;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected_Server;

            if (_spawnPoints == null)
            {
                _spawnPoints = FindFirstObjectByType<StageSpawnPoints>();
                if (_spawnPoints == null)
                    Debug.LogWarning("[StageProgress] SpawnPoints fallback 발생: StageSpawnPoints not found in scene.");
            }
        }

        HookGameSessionStateOnce();
    }

    public override void OnNetworkDespawn()
    {
        UnhookGameSessionState();

        if (!IsServer) return;

        if (NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnected_Server;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected_Server;
        }
    }

    // =========================================
    // Hook GameSession State (Single Source of Truth)
    // =========================================
    private void HookGameSessionStateOnce()
    {
        if (_isSessionHooked) return;
        _isSessionHooked = true;

        var session = GameSessionController.Instance;
        if (session == null)
        {
            Debug.LogWarning("[StageProgress] HookGameSession fallback 발생: GameSessionController.Instance is null.");
            return;
        }

        session.AddStateListener(OnGameSessionStateChanged_Any);
    }

    private void UnhookGameSessionState()
    {
        if (!_isSessionHooked) return;
        _isSessionHooked = false;

        var session = GameSessionController.Instance;
        if (session == null) return;

        session.RemoveStateListener(OnGameSessionStateChanged_Any);
    }

    private void OnGameSessionStateChanged_Any(E_GameSessionState prev, E_GameSessionState next)
    {
        // Gate 제어는 서버가 결정해서 RPC로 뿌린다.
        if (!IsServer) return;

        switch (next)
        {
            case E_GameSessionState.Lobby:
                // 세션 리셋과 동일 타이밍: 입력 열기 + 스테이지 진행 데이터 리셋
                SetGateForLocalPlayerRpc(true, E_InputGateReason.Lobby);
                _currentStageIndex = 0;

                RebuildFromConnectedClients();
                Debug.Log("[StageProgress] Sync by GameSession: Lobby -> Gate Close + Rebuild.");
                break;

            case E_GameSessionState.Countdown:
                // 스테이지 시작 준비(워프/서버 이동 차단은 별도 시스템에서)
                SetGateForLocalPlayerRpc(false, E_InputGateReason.Countdown);
                UpdateStageIndexForCountdown(prev);
                WarpPlayersToSpawnPoints_Server("countdown_enter");

                Debug.Log("[StageProgress] Sync by GameSession: Countdown -> Gate Close.");
                break;

            case E_GameSessionState.Running:
                // 달리기 시작
                SetGateForLocalPlayerRpc(true, E_InputGateReason.Run);
                RecordStageRunStartTime_Server(_currentStageIndex);

                Debug.Log("[StageProgress] Sync by GameSession: Running -> Gate Open.");
                break;

            case E_GameSessionState.Result:
                SetGateForLocalPlayerRpc(false, E_InputGateReason.Result);
                Debug.Log("[StageProgress] Sync by GameSession: Result -> Gate Close.");
                break;

            default:
                Debug.LogWarning($"[StageProgress] GameSessionState fallback 발생: unknown state={next}");
                break;
        }
    }

    private void UpdateStageIndexForCountdown(E_GameSessionState prev)
    {
        if (!IsServer) return;

        if (prev == E_GameSessionState.Lobby)
        {
            _currentStageIndex = 0;
            return;
        }

        if (prev == E_GameSessionState.Result)
        {
            _currentStageIndex = Mathf.Clamp(_currentStageIndex + 1, 0, Mathf.Max(0, _stagesToFinish - 1));
        }
    }

    // =========================================
    // Client -> Server: stage complete notify (Goal 도달 보고)
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

        if (stageIndex >= _stagesToFinish)
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: stageIndex out of range. stageIndex={stageIndex}, stagesToFinish={_stagesToFinish}");
            return;
        }

        ulong sender = rpcParams.Receive.SenderClientId;

        if (!NetworkManager.ConnectedClientsIds.Contains(sender))
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: sender not connected. sender={sender}");
            return;
        }

        // Running이 아닌데 들어오면 상태 꼬임 신호. 무시하진 않되 Warning은 찍는다.
        var session = GameSessionController.Instance;
        if (session == null)
        {
            Debug.LogWarning("[StageProgress] ReportStageCleared fallback 발생: GameSessionController.Instance is null.");
            return;
        }

        if (session.State != E_GameSessionState.Running)
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: received while session.State={session.State}. sender={sender}, stageIndex={stageIndex}");
        }

        if (stageIndex != _currentStageIndex)
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: stageIndex mismatch. currentStage={_currentStageIndex}, reported={stageIndex}, sender={sender}");
            return;
        }

        if (!_clearedStageSetByClient.TryGetValue(sender, out var set))
        {
            set = new HashSet<int>();
            _clearedStageSetByClient[sender] = set;
            _clearedCountByClient[sender] = 0;
        }

        // 중복 완료(=resolved) 방지
        if (!set.Add(stageIndex))
        {
            Debug.LogWarning($"[StageProgress] ReportStageCleared fallback 발생: duplicate resolved ignored. sender={sender}, stageIndex={stageIndex}");
            return;
        }

        double now = NetworkManager.ServerTime.Time;

        // ===== 핵심 변경: 첫 Goal 도착 시점부터 60초 시작 =====
        // 늦게 보고한 경우 Retire 처리 + 기록=firstGoal+90초
        RegisterGoalArrivalOrRetire_Server(sender, stageIndex, now);

        int newCount = _clearedCountByClient[sender] + 1;
        _clearedCountByClient[sender] = newCount;

        Debug.Log($"[StageProgress] Stage resolved(Goal/Retire). sender={sender}, stageIndex={stageIndex}, resolvedCount={newCount}/{_stagesToFinish}");

        // Goal/Retire 처리 즉시: 해당 플레이어 입력 잠금(개별)
        CloseGateForClient_Server(sender, E_InputGateReason.Goal);
        StopPlayerMovement_Server(sender, "stage_resolved");

        // ===== 전원 Resolved -> Result 요청 =====
        if (IsAllConnectedPlayersResolvedStage_Server(stageIndex))
        {
            // 중복 Result 진입 방지
            if (_resultRequestedStages.Add(stageIndex))
            {
                // Result는 GameSession이 단일 진실
                session.RequestEnterResult_Server("all_players_resolved_stage");

                Debug.Log($"[StageProgress] All players resolved stage -> RequestEnterResult. stageIndex={stageIndex}");
            }
        }

        // ===== 전원 모든 스테이지 완료 -> EndMatch =====
        if (IsAllConnectedPlayersFinished_Server())
        {
            EndMatch_Server("all_players_finished");
        }
    }

    // =========================================
    // Goal Window / Retire rules
    // =========================================
    private void RegisterGoalArrivalOrRetire_Server(ulong sender, int stageIndex, double reportTime)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[StageProgress] RegisterGoalArrivalOrRetire fallback 발생: called on non-server.");
            return;
        }

        float windowSec = Mathf.Max(0f, _goalWindowSeconds);
        float retirePenaltySec = Mathf.Max(0f, _retirePenaltySeconds);

        if (!_goalWindowByStage.TryGetValue(stageIndex, out var windowInfo))
        {
            windowInfo = new StageGoalWindowInfo
            {
                hasFirstArrival = false,
                firstArriver = 0,
                firstGoalServerTime = 0.0,
                goalWindowEndServerTime = 0.0,
                retireResolvedApplied = false,
            };
        }

        // 첫 Goal 도착이면 여기서 윈도우 시작(=firstGoal + 60)
        if (!windowInfo.hasFirstArrival)
        {
            windowInfo.hasFirstArrival = true;
            windowInfo.firstArriver = sender;
            windowInfo.firstGoalServerTime = reportTime;
            windowInfo.goalWindowEndServerTime = reportTime + windowSec;
            windowInfo.retireResolvedApplied = false;

            Debug.Log($"[StageProgress] First goal arrival -> start window. stageIndex={stageIndex}, sender={sender}, firstGoalAt={reportTime:F3}, windowEndAt={windowInfo.goalWindowEndServerTime:F3}");
        }

        _goalWindowByStage[stageIndex] = windowInfo;

        if (!_arrivalByStage.TryGetValue(stageIndex, out var arrivals))
        {
            arrivals = new Dictionary<ulong, StageArrivalInfo>();
            _arrivalByStage[stageIndex] = arrivals;
        }

        if (arrivals.ContainsKey(sender))
        {
            Debug.LogWarning($"[StageProgress] RegisterGoalArrivalOrRetire fallback 발생: arrival already recorded. sender={sender}, stageIndex={stageIndex}");
            return;
        }

        bool withinWindow = reportTime <= windowInfo.goalWindowEndServerTime;
        bool retired = !withinWindow;

        double finalRecordServerTime = retired
            ? (windowInfo.firstGoalServerTime + retirePenaltySec)
            : reportTime;
        double stageRunStartTime = GetStageRunStartTimeOrFallback(stageIndex, reportTime);
        double finalRecordSecondsFromStageStart = Mathf.Max(0f, (float)(finalRecordServerTime - stageRunStartTime));

        arrivals[sender] = new StageArrivalInfo
        {
            reportServerTime = reportTime,
            finalRecordSecondsFromStageStart = finalRecordSecondsFromStageStart,
            withinGoalWindow = withinWindow,
            retired = retired,
        };

        if (retired)
        {
            Debug.LogWarning($"[StageProgress] Retire(late goal). sender={sender}, stageIndex={stageIndex}, report={reportTime:F3}, windowEnd={windowInfo.goalWindowEndServerTime:F3}, record={finalRecordSecondsFromStageStart:F3}s");
        }
    }

    private void ResolveGoalWindowTimeouts_Server()
    {
        if (!IsServer) return;
        if (_endTriggered) return;

        // 현재 스테이지만 처리(요구사항: 첫 goal부터 60초 제한)
        int stageIndex = _currentStageIndex;

        if (!_goalWindowByStage.TryGetValue(stageIndex, out var windowInfo))
            return;

        if (!windowInfo.hasFirstArrival)
            return;

        if (windowInfo.retireResolvedApplied)
            return;

        double now = NetworkManager != null ? NetworkManager.ServerTime.Time : 0.0;
        if (now <= windowInfo.goalWindowEndServerTime)
            return;

        // 윈도우 만료: 아직 도착 보고 없는 플레이어는 Retire로 처리 + 기록=firstGoal+90
        if (!_arrivalByStage.TryGetValue(stageIndex, out var arrivals))
        {
            arrivals = new Dictionary<ulong, StageArrivalInfo>();
            _arrivalByStage[stageIndex] = arrivals;
        }

        float retirePenaltySec = Mathf.Max(0f, _retirePenaltySeconds);
        double finalRecordServerTime = windowInfo.firstGoalServerTime + retirePenaltySec;
        double stageRunStartTime = GetStageRunStartTimeOrFallback(stageIndex, finalRecordServerTime);
        double finalRecordSecondsFromStageStart = Mathf.Max(0f, (float)(finalRecordServerTime - stageRunStartTime));

        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            // 이미 resolved(Goal/Retire) 된 플레이어는 스킵
            if (_clearedStageSetByClient.TryGetValue(id, out var set) && set.Contains(stageIndex))
                continue;

            // 아직 arrivals에 없으면 "미도착" -> Retire 확정
            if (!arrivals.ContainsKey(id))
            {
                arrivals[id] = new StageArrivalInfo
                {
                    reportServerTime = 0.0,
                    finalRecordSecondsFromStageStart = finalRecordSecondsFromStageStart,
                    withinGoalWindow = false,
                    retired = true,
                };

                // resolved로 카운트 처리
                if (!_clearedStageSetByClient.TryGetValue(id, out var s))
                {
                    s = new HashSet<int>();
                    _clearedStageSetByClient[id] = s;
                    _clearedCountByClient[id] = 0;
                }

                if (s.Add(stageIndex))
                {
                    _clearedCountByClient[id] = _clearedCountByClient[id] + 1;

                    Debug.LogWarning($"[StageProgress] Retire(timeout). clientId={id}, stageIndex={stageIndex}, windowEnd={windowInfo.goalWindowEndServerTime:F3}, record={finalRecordSecondsFromStageStart:F3}s");

                    CloseGateForClient_Server(id, E_InputGateReason.Goal);
                    StopPlayerMovement_Server(id, "retire_timeout");
                }
            }
        }

        windowInfo.retireResolvedApplied = true;
        _goalWindowByStage[stageIndex] = windowInfo;

        // 전원 resolved면 Result 요청(타임아웃에 의해 충족될 수 있음)
        var session = GameSessionController.Instance;
        if (session == null)
        {
            Debug.LogWarning("[StageProgress] ResolveGoalWindowTimeouts fallback 발생: GameSessionController.Instance is null.");
            return;
        }

        if (IsAllConnectedPlayersResolvedStage_Server(stageIndex))
        {
            if (_resultRequestedStages.Add(stageIndex))
            {
                session.RequestEnterResult_Server("goal_window_timeout_resolved");
                Debug.Log($"[StageProgress] Timeout resolved all -> RequestEnterResult. stageIndex={stageIndex}");
            }
        }

        if (IsAllConnectedPlayersFinished_Server())
        {
            EndMatch_Server("all_players_finished");
        }
    }

    // =========================================
    // Server: Stage result ranking
    // =========================================
    public bool TryBuildStageRanking_Server(int stageIndex, out List<StageResultEntry> ranking, bool includeRetired = true)
    {
        ranking = new List<StageResultEntry>();

        if (!IsServer)
        {
            Debug.LogWarning("[StageProgress] TryBuildStageRanking fallback 발생: called on non-server.");
            return false;
        }

        if (stageIndex < 0 || stageIndex >= _stagesToFinish)
        {
            Debug.LogWarning($"[StageProgress] TryBuildStageRanking fallback 발생: invalid stageIndex={stageIndex}");
            return false;
        }

        if (!_arrivalByStage.TryGetValue(stageIndex, out var arrivals))
        {
            Debug.LogWarning($"[StageProgress] TryBuildStageRanking fallback 발생: no arrivals for stageIndex={stageIndex}");
            return false;
        }

        if (!IsAllConnectedPlayersResolvedStage_Server(stageIndex))
        {
            Debug.LogWarning($"[StageProgress] TryBuildStageRanking fallback 발생: not all resolved. stageIndex={stageIndex}");
            return false;
        }

        IEnumerable<KeyValuePair<ulong, StageArrivalInfo>> arrivalPairs = arrivals;
        if (!includeRetired)
            arrivalPairs = arrivalPairs.Where(pair => !pair.Value.retired);

        var ordered = arrivalPairs
            .OrderBy(pair => pair.Value.finalRecordSecondsFromStageStart)
            .ThenBy(pair => pair.Value.reportServerTime)
            .ThenBy(pair => pair.Key)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            var pair = ordered[i];
            ranking.Add(new StageResultEntry(pair.Key, i + 1, pair.Value));
        }

        return true;
    }

    // =========================================
    // Gate RPCs (Server authoritative trigger)
    // =========================================
    // 전원 공통: 각 클라(Host 포함)가 "자기 로컬 플레이어"의 Gate만 토글
    [Rpc(SendTo.ClientsAndHost)]
    private void SetGateForLocalPlayerRpc(bool open, E_InputGateReason reason, RpcParams rpcParams = default)
    {
        var nm = NetworkManager;
        if (nm == null || nm.LocalClient == null)
        {
            Debug.LogWarning("[StageProgress] SetGateForLocalPlayer fallback 발생: NetworkManager/LocalClient is null.");
            return;
        }

        var player = nm.LocalClient.PlayerObject;
        if (player == null)
        {
            Debug.LogWarning("[StageProgress] SetGateForLocalPlayer fallback 발생: LocalClient.PlayerObject is null.");
            return;
        }

        var gate = player.GetComponent<PlayerInputGate>();
        if (gate == null)
        {
            Debug.LogWarning("[StageProgress] SetGateForLocalPlayer fallback 발생: PlayerInputGate missing on local player.");
            return;
        }

        if (open) gate.Open(reason);
        else gate.Close(reason);
    }

    // 특정 클라 타겟: Goal 도달 잠금 등
    [Rpc(SendTo.SpecifiedInParams)]
    private void CloseGate_TargetRpc(E_InputGateReason reason, RpcParams rpcParams)
    {
        var nm = NetworkManager;
        if (nm == null || nm.LocalClient == null)
        {
            Debug.LogWarning("[StageProgress] CloseGate_Target fallback 발생: NetworkManager/LocalClient is null.");
            return;
        }

        var player = nm.LocalClient.PlayerObject;
        if (player == null)
        {
            Debug.LogWarning("[StageProgress] CloseGate_Target fallback 발생: LocalClient.PlayerObject is null.");
            return;
        }

        var gate = player.GetComponent<PlayerInputGate>();
        if (gate == null)
        {
            Debug.LogWarning("[StageProgress] CloseGate_Target fallback 발생: PlayerInputGate missing on local player.");
            return;
        }

        gate.Close(reason);
    }

    private void CloseGateForClient_Server(ulong clientId, E_InputGateReason reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[StageProgress] CloseGateForClient fallback 발생: called on non-server.");
            return;
        }

        // 2.8 문서 권장: SendTo.SpecifiedInParams + RpcTarget.Single(...)로 타겟 전달
        CloseGate_TargetRpc(reason, RpcTarget.Single(clientId, RpcTargetUse.Temp));
    }

    private void WarpPlayersToSpawnPoints_Server(string reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[StageProgress] Warp fallback 발생: called on non-server.");
            return;
        }

        if (!_warpOnCountdown) return;

        var nm = NetworkManager;
        if (nm == null)
        {
            Debug.LogWarning("[StageProgress] Warp fallback 발생: NetworkManager is null.");
            return;
        }

        if (_spawnPoints == null)
        {
            Debug.LogWarning("[StageProgress] Warp fallback 발생: StageSpawnPoints is null.");
            return;
        }

        var orderedClients = nm.ConnectedClientsIds.OrderBy(id => id).ToList();

        for (int i = 0; i < orderedClients.Count; i++)
        {
            ulong clientId = orderedClients[i];
            if (!nm.ConnectedClients.TryGetValue(clientId, out var client))
            {
                Debug.LogWarning($"[StageProgress] Warp fallback 발생: client not found. clientId={clientId}");
                continue;
            }

            var player = client.PlayerObject;
            if (player == null)
            {
                Debug.LogWarning($"[StageProgress] Warp fallback 발생: PlayerObject is null. clientId={clientId}");
                continue;
            }

            if (!_spawnPoints.TryGetSpawnPoint(_currentStageIndex, i, out var spawn))
            {
                spawn = _spawnPoints.FallbackSpawn;
                if (spawn == null)
                {
                    Debug.LogWarning($"[StageProgress] Warp fallback 발생: spawn point missing. stage={_currentStageIndex}, slot={i}, clientId={clientId}");
                    continue;
                }

                Debug.LogWarning($"[StageProgress] Warp fallback 발생: using fallback spawn. stage={_currentStageIndex}, slot={i}, clientId={clientId}");
            }

            var netTransform = player.GetComponent<NetworkTransform>();
            if (netTransform != null)
            {
                netTransform.Teleport(spawn.position, spawn.rotation, player.transform.localScale);
            }
            else
            {
                player.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            }

            var respawn = player.GetComponent<PlayerRespawnServer>();
            if (respawn != null)
            {
                respawn.SetStageStartCheckpoint_Server(spawn.position, spawn.rotation, GetStageCheckpointIndexBase());
            }

            var motor = player.GetComponent<PlayerMotorServer>();
            if (motor != null)
            {
                motor.StopImmediately_Server($"warp:{reason}");
                continue;
            }

            var rb = player.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        Debug.Log($"[StageProgress] WarpPlayersToSpawnPoints done. stage={_currentStageIndex}, reason={reason}");
    }

    private int GetStageCheckpointIndexBase()
    {
        return _stageCheckpointIndexBase + (_currentStageIndex * _stageCheckpointIndexStride);
    }

    private void StopPlayerMovement_Server(ulong clientId, string reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[StageProgress] StopPlayerMovement fallback 발생: called on non-server.");
            return;
        }

        var nm = NetworkManager;
        if (nm == null)
        {
            Debug.LogWarning("[StageProgress] StopPlayerMovement fallback 발생: NetworkManager is null.");
            return;
        }

        if (!nm.ConnectedClients.TryGetValue(clientId, out var client))
        {
            Debug.LogWarning($"[StageProgress] StopPlayerMovement fallback 발생: client not found. clientId={clientId}");
            return;
        }

        var player = client.PlayerObject;
        if (player == null)
        {
            Debug.LogWarning($"[StageProgress] StopPlayerMovement fallback 발생: PlayerObject is null. clientId={clientId}");
            return;
        }

        var motor = player.GetComponent<PlayerMotorServer>();
        if (motor == null)
        {
            Debug.LogWarning($"[StageProgress] StopPlayerMovement fallback 발생: PlayerMotorServer missing. clientId={clientId}");
            return;
        }

        motor.StopImmediately_Server(reason);
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

        // 전원 잠금(안전)
        SetGateForLocalPlayerRpc(false, E_InputGateReason.Result);

        var session = GameSessionController.Instance;
        if (session == null)
        {
            Debug.LogWarning("[StageProgress] EndMatch fallback 발생: GameSessionController.Instance is null.");
            return;
        }

        // 세션 리셋 (서버)
        session.ResetSessionToLobby_Server("match_end");

        // 로비 Unlock (Host만 동작, 내부에서 Host 체크/로그 처리)
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
        _resultRequestedStages.Clear();
        _goalWindowByStage.Clear();
        _arrivalByStage.Clear();
        _stageRunStartTimeByStage.Clear();

        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            _clearedCountByClient[id] = 0;
            _clearedStageSetByClient[id] = new HashSet<int>();
        }

        _endTriggered = false;

        Debug.Log($"[StageProgress] RebuildFromConnectedClients. connected={NetworkManager.ConnectedClientsIds.Count}");
    }

    private void RecordStageRunStartTime_Server(int stageIndex)
    {
        if (!IsServer) return;

        var nm = NetworkManager;
        if (nm == null)
        {
            Debug.LogWarning("[StageProgress] RecordStageRunStartTime fallback 발생: NetworkManager is null.");
            return;
        }

        double startTime = nm.ServerTime.Time;
        _stageRunStartTimeByStage[stageIndex] = startTime;

        Debug.Log($"[StageProgress] Stage run start time recorded. stageIndex={stageIndex}, startTime={startTime:F3}");
    }

    private double GetStageRunStartTimeOrFallback(int stageIndex, double fallbackTime)
    {
        if (_stageRunStartTimeByStage.TryGetValue(stageIndex, out double startTime))
        {
            return startTime;
        }

        Debug.LogWarning($"[StageProgress] Stage run start time missing. stageIndex={stageIndex}, fallbackTime={fallbackTime:F3}");
        _stageRunStartTimeByStage[stageIndex] = fallbackTime;
        return fallbackTime;
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

    // "Goal 도달"이 아니라 "Resolved(Goal/Retire)" 기준
    private bool IsAllConnectedPlayersResolvedStage_Server(int stageIndex)
    {
        foreach (var id in NetworkManager.ConnectedClientsIds)
        {
            if (!_clearedStageSetByClient.TryGetValue(id, out var set))
            {
                Debug.LogWarning($"[StageProgress] StageAllCheck fallback 발생: missing stage set. clientId={id}");
                return false;
            }

            if (!set.Contains(stageIndex))
                return false;
        }

        return true;
    }
}
