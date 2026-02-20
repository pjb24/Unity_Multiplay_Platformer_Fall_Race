using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

public sealed class StageProgressController : NetworkBehaviour
{
    /// <summary>
    /// 현재 씬의 StageProgressController 싱글턴 인스턴스입니다.
    /// </summary>
    public static StageProgressController Instance { get; private set; }

    [SerializeField] private int _stagesToFinish = 3;
    [SerializeField] private StageSpawnPoints _spawnPoints;
    [SerializeField] private bool _warpOnCountdown = true;
    [SerializeField] private int _stageCheckpointIndexBase = 1000;
    [SerializeField] private int _stageCheckpointIndexStride = 1000;

    [Header("Goal Window")]
    [SerializeField] private float _goalWindowSeconds = 60f;

    [Header("Retire Rule")]
    [SerializeField] private float _retirePenaltySeconds = 90f;

    [Header("Final Stage Result")]
    /// <summary>
    /// 마지막 스테이지 종료 후 결과표를 확인할 수 있도록 유지할 대기 시간(초)입니다.
    /// </summary>
    [SerializeField] private float _finalStageResultWaitSeconds = 3f;

    /// <summary>
    /// 기록 미입력 상태를 표현하기 위한 센티넬 값입니다.
    /// </summary>
    public const float MissingRecordValue = -1f;

    /// <summary>
    /// 서버 원본 기록(클라이언트 동기화용) 목록입니다.
    /// </summary>
    private readonly NetworkList<PlayerRaceRecordNet> _raceRecords = new NetworkList<PlayerRaceRecordNet>();

    /// <summary>
    /// 현재 스테이지 Run 시작 서버 시각(NetworkTime)입니다.
    /// </summary>
    private readonly NetworkVariable<double> _currentStageStartAtServerTime = new NetworkVariable<double>(0.0);

    /// <summary>
    /// 현재 스테이지 인덱스입니다(클라이언트 UI 동기화용).
    /// </summary>
    private readonly NetworkVariable<int> _currentStageIndexNet = new NetworkVariable<int>(0);

    /// <summary>
    /// 첫 도착 이후 목표 윈도우 종료 서버 시각(NetworkTime)입니다.
    /// </summary>
    private readonly NetworkVariable<double> _goalWindowEndAtServerTime = new NetworkVariable<double>(0.0);

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

    /// <summary>
    /// 마지막 스테이지 Result 상태에서 매치를 종료할 서버 시각(NetworkTime)입니다.
    /// </summary>
    private double _finalStageResultEndAtServerTime;

    /// <summary>
    /// 마지막 스테이지 Result 대기 종료 타이머 활성화 여부입니다.
    /// </summary>
    private bool _isFinalStageResultWaitArmed;

    /// <summary>
    /// 클라이언트별 기록 데이터 인덱스 캐시입니다.
    /// </summary>
    private readonly Dictionary<ulong, int> _recordIndexByClient = new Dictionary<ulong, int>(8);

    /// <summary>
    /// 승인 페이로드에서 수집한 클라이언트별 표시 이름 캐시입니다.
    /// </summary>
    private readonly Dictionary<ulong, string> _approvedDisplayNameByClient = new Dictionary<ulong, string>(8);

    /// <summary>
    /// 클라이언트 HUD 정렬용 레이스 기록 스냅샷 재사용 버퍼입니다.
    /// </summary>
    private readonly List<PlayerRaceRecordSnapshot> _raceRecordSnapshotBuffer = new List<PlayerRaceRecordSnapshot>(8);

    /// <summary>
    /// 클라이언트 HUD 정렬에 사용하는 비교기 인스턴스입니다.
    /// </summary>
    private readonly RaceRecordSnapshotComparer _raceRecordSnapshotComparer = new RaceRecordSnapshotComparer();

    public struct PlayerRaceRecordNet : INetworkSerializable, System.IEquatable<PlayerRaceRecordNet>
    {
        /// <summary>
        /// 플레이어 클라이언트 식별자입니다.
        /// </summary>
        public ulong clientId;

        /// <summary>
        /// 플레이어 표시 이름입니다.
        /// </summary>
        public FixedString64Bytes displayName;

        /// <summary>
        /// Stage1 기록(초)입니다. 미기록은 MissingRecordValue입니다.
        /// </summary>
        public float stage1Seconds;

        /// <summary>
        /// Stage2 기록(초)입니다. 미기록은 MissingRecordValue입니다.
        /// </summary>
        public float stage2Seconds;

        /// <summary>
        /// Stage3 기록(초)입니다. 미기록은 MissingRecordValue입니다.
        /// </summary>
        public float stage3Seconds;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref clientId);
            serializer.SerializeValue(ref displayName);
            serializer.SerializeValue(ref stage1Seconds);
            serializer.SerializeValue(ref stage2Seconds);
            serializer.SerializeValue(ref stage3Seconds);
        }

        public bool Equals(PlayerRaceRecordNet other)
        {
            return clientId == other.clientId
                && displayName.Equals(other.displayName)
                && Mathf.Approximately(stage1Seconds, other.stage1Seconds)
                && Mathf.Approximately(stage2Seconds, other.stage2Seconds)
                && Mathf.Approximately(stage3Seconds, other.stage3Seconds);
        }
    }

    public readonly struct PlayerRaceRecordSnapshot
    {
        public PlayerRaceRecordSnapshot(ulong clientId, string displayName, float stage1, float stage2, float stage3)
        {
            // 결과 테이블 행이 참조하는 플레이어 식별자입니다.
            ClientId = clientId;
            // 결과 테이블 행이 참조하는 플레이어 표시 이름입니다.
            DisplayName = displayName;
            // Stage 1 기록 원본 값입니다.
            Stage1Seconds = stage1;
            // Stage 2 기록 원본 값입니다.
            Stage2Seconds = stage2;
            // Stage 3 기록 원본 값입니다.
            Stage3Seconds = stage3;
            // Stage 1 클리어 여부입니다.
            HasStage1 = stage1 >= 0f;
            // Stage 2 클리어 여부입니다.
            HasStage2 = stage2 >= 0f;
            // Stage 3 클리어 여부입니다.
            HasStage3 = stage3 >= 0f;

            // 합산 수식 계산에 사용할 스테이지 기록 배열입니다.
            float[] stageSeconds = { stage1, stage2, stage3 };
            CompletedStageCount = CountRecordedStages(stageSeconds);
            TotalSeconds = CalculateTotalSeconds(stageSeconds);
            HasAnyStageRecorded = CompletedStageCount > 0;
            IsAllStagesRecorded = CompletedStageCount == stageSeconds.Length;
        }

        public ulong ClientId { get; }
        public string DisplayName { get; }
        public float Stage1Seconds { get; }
        public float Stage2Seconds { get; }
        public float Stage3Seconds { get; }
        public bool HasStage1 { get; }
        public bool HasStage2 { get; }
        public bool HasStage3 { get; }
        public int CompletedStageCount { get; }
        public float TotalSeconds { get; }
        public bool HasAnyStageRecorded { get; }
        public bool IsAllStagesRecorded { get; }

        /// <summary>
        /// 기록이 존재하는 스테이지 개수를 계산합니다.
        /// </summary>
        private static int CountRecordedStages(float[] stageSeconds)
        {
            // 기록이 존재하는 스테이지 개수 누적 변수입니다.
            int count = 0;
            for (int i = 0; i < stageSeconds.Length; i++)
            {
                if (stageSeconds[i] >= 0f)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Total 계산 수식을 통해 기록이 존재하는 스테이지 시간만 합산합니다.
        /// </summary>
        private static float CalculateTotalSeconds(float[] stageSeconds)
        {
            // Total 계산 결과 누적 변수입니다.
            float sum = 0f;
            for (int i = 0; i < stageSeconds.Length; i++)
            {
                if (stageSeconds[i] < 0f)
                    continue;

                sum += Mathf.Max(0f, stageSeconds[i]);
            }
            return sum;
        }
    }

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

    /// <summary>
    /// 현재 서버 기준 스테이지 인덱스를 반환합니다.
    /// </summary>
    public int CurrentStageIndex => _currentStageIndexNet.Value;

    /// <summary>
    /// 전체 스테이지 개수를 반환합니다.
    /// </summary>
    public int StagesToFinish => _stagesToFinish;

    /// <summary>
    /// 현재 스테이지의 시작 서버 시각을 반환합니다.
    /// </summary>
    public double CurrentStageStartAtServerTime => _currentStageStartAtServerTime.Value;

    /// <summary>
    /// 현재 스테이지의 목표 윈도우 종료 서버 시각을 반환합니다.
    /// </summary>
    public double GoalWindowEndAtServerTime => _goalWindowEndAtServerTime.Value;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[StageProgress] Awake fallback 발생: duplicate StageProgressController. Destroy duplicate.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        if (!IsServer) return;
        ResolveGoalWindowTimeouts_Server();
        ProcessFinalStageResultWait_Server();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            RebuildFromConnectedClients();
            RebuildRaceRecordTable_Server();

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
                _currentStageIndexNet.Value = _currentStageIndex;
                _goalWindowEndAtServerTime.Value = 0.0;

                RebuildFromConnectedClients();
                RebuildRaceRecordTable_Server();
                DisarmFinalStageResultWait_Server();
                Debug.Log("[StageProgress] Sync by GameSession: Lobby -> Gate Close + Rebuild.");
                break;

            case E_GameSessionState.Countdown:
                // 스테이지 시작 준비(워프/서버 이동 차단은 별도 시스템에서)
                SetGateForLocalPlayerRpc(false, E_InputGateReason.Countdown);
                UpdateStageIndexForCountdown(prev);
                WarpPlayersToSpawnPoints_Server("countdown_enter");
                DisarmFinalStageResultWait_Server();

                Debug.Log("[StageProgress] Sync by GameSession: Countdown -> Gate Close.");
                break;

            case E_GameSessionState.Running:
                // 달리기 시작
                SetGateForLocalPlayerRpc(true, E_InputGateReason.Run);
                RecordStageRunStartTime_Server(_currentStageIndex);
                DisarmFinalStageResultWait_Server();

                Debug.Log("[StageProgress] Sync by GameSession: Running -> Gate Open.");
                break;

            case E_GameSessionState.Result:
                SetGateForLocalPlayerRpc(false, E_InputGateReason.Result);
                ApplyResultFeelingToPlayers_Server();
                TryArmFinalStageResultWait_Server();
                Debug.Log("[StageProgress] Sync by GameSession: Result -> Gate Close.");
                break;

            default:
                Debug.LogWarning($"[StageProgress] GameSessionState fallback 발생: unknown state={next}");
                break;
        }
    }

    /// <summary>
    /// 경기 종료 시 최하위 순위만 lose(1), 나머지 순위는 win(0) feel 연출을 동기화합니다.
    /// 동점 처리는 CompletedStageCount + TotalSeconds가 같은 경우 동일 rank로 간주합니다.
    /// </summary>
    private void ApplyResultFeelingToPlayers_Server()
    {
        if (!IsServer)
            return;

        if (!TryGetRaceRecordsSnapshot(out List<PlayerRaceRecordSnapshot> orderedRecords) || orderedRecords == null || orderedRecords.Count == 0)
            return;

        // 현재 스테이지 결과 연출 대상 전체 인원 수입니다.
        int totalPlayers = orderedRecords.Count;

        // 최하위 순위(예: 2인=2등, 3인=3등, 4인=4등)를 계산하기 위한 기준 rank입니다.
        int loseRank = Mathf.Max(1, totalPlayers);

        int currentRank = 1;
        for (int i = 0; i < orderedRecords.Count; i++)
        {
            PlayerRaceRecordSnapshot record = orderedRecords[i];
            if (i > 0)
            {
                PlayerRaceRecordSnapshot prev = orderedRecords[i - 1];
                bool isTie = prev.CompletedStageCount == record.CompletedStageCount
                    && Mathf.Approximately(prev.TotalSeconds, record.TotalSeconds);
                if (!isTie)
                    currentRank = i + 1;
            }

            // 최하위 순위면 lose(1), 그 외 순위는 win(0) 블렌드 값입니다.
            float blendFeel = currentRank >= loseRank ? 1f : 0f;

            if (!NetworkManager.ConnectedClients.TryGetValue(record.ClientId, out NetworkClient networkClient))
                continue;

            NetworkObject playerObject = networkClient.PlayerObject;
            if (playerObject == null)
                continue;

            PlayerMotorServer motor = playerObject.GetComponent<PlayerMotorServer>();
            if (motor == null)
                continue;

            motor.SetResultFeeling_Server(blendFeel);
        }
    }

    private void UpdateStageIndexForCountdown(E_GameSessionState prev)
    {
        if (!IsServer) return;

        if (prev == E_GameSessionState.Lobby)
        {
            _currentStageIndex = 0;
            _currentStageIndexNet.Value = _currentStageIndex;
            _goalWindowEndAtServerTime.Value = 0.0;

            return;
        }

        if (prev == E_GameSessionState.Result)
        {
            _currentStageIndex = Mathf.Clamp(_currentStageIndex + 1, 0, Mathf.Max(0, _stagesToFinish - 1));
            _currentStageIndexNet.Value = _currentStageIndex;
            _goalWindowEndAtServerTime.Value = 0.0;
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
        ApplyStageRecordToRaceTable_Server(sender, stageIndex);

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
            _goalWindowEndAtServerTime.Value = windowInfo.goalWindowEndServerTime;

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

                    ApplyStageRecordToRaceTable_Server(id, stageIndex);
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
    }

    /// <summary>
    /// 마지막 스테이지 결과 상태 진입 시 결과표 확인 대기 타이머를 활성화합니다.
    /// </summary>
    private void TryArmFinalStageResultWait_Server()
    {
        if (!IsServer)
            return;

        if (_isFinalStageResultWaitArmed)
            return;

        if (_stagesToFinish <= 0)
            return;

        if (_currentStageIndex < _stagesToFinish - 1)
            return;

        if (!IsAllConnectedPlayersFinished_Server())
            return;

        double now = NetworkManager.ServerTime.Time;
        _finalStageResultEndAtServerTime = now + Mathf.Max(0f, _finalStageResultWaitSeconds);
        _isFinalStageResultWaitArmed = true;

        Debug.Log($"[StageProgress] Final stage result wait armed. now={now:F3}, endAt={_finalStageResultEndAtServerTime:F3}");
    }

    /// <summary>
    /// 마지막 스테이지 결과표 확인 대기 타이머 만료 여부를 검사하고 매치를 종료합니다.
    /// </summary>
    private void ProcessFinalStageResultWait_Server()
    {
        if (!IsServer)
            return;

        if (!_isFinalStageResultWaitArmed)
            return;

        var session = GameSessionController.Instance;
        if (session == null)
            return;

        if (session.State != E_GameSessionState.Result)
            return;

        double now = NetworkManager.ServerTime.Time;
        if (now < _finalStageResultEndAtServerTime)
            return;

        DisarmFinalStageResultWait_Server();
        EndMatch_Server("final_stage_result_wait_elapsed");
    }

    /// <summary>
    /// 마지막 스테이지 결과표 확인 대기 타이머를 비활성화하고 시간을 초기화합니다.
    /// </summary>
    private void DisarmFinalStageResultWait_Server()
    {
        if (!IsServer)
            return;

        _isFinalStageResultWaitArmed = false;
        _finalStageResultEndAtServerTime = 0.0;
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
        _currentStageStartAtServerTime.Value = 0.0;
        _goalWindowEndAtServerTime.Value = 0.0;
        _finalStageResultEndAtServerTime = 0.0;
        _isFinalStageResultWaitArmed = false;

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
        _currentStageStartAtServerTime.Value = startTime;

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
        AddRaceRecordIfMissing_Server(clientId);

        Debug.Log($"[StageProgress] Client connected tracked. clientId={clientId}");
    }

    private void OnClientDisconnected_Server(ulong clientId)
    {
        if (_clearedCountByClient.Remove(clientId))
        {
            _clearedStageSetByClient.Remove(clientId);
            _approvedDisplayNameByClient.Remove(clientId);
            RemoveRaceRecord_Server(clientId);
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

    /// <summary>
    /// 승인 단계에서 전달된 표시 이름을 서버 캐시에 저장합니다.
    /// </summary>
    public void CacheApprovedDisplayName_Server(ulong clientId, string rawDisplayName)
    {
        if (!IsServer)
            return;

        // 승인 페이로드에서 받은 원본 이름을 정규화한 결과 문자열입니다.
        string sanitizedName = DisplayNamePolicy.Sanitize(rawDisplayName);
        _approvedDisplayNameByClient[clientId] = sanitizedName;

        if (_recordIndexByClient.TryGetValue(clientId, out int existingIndex))
        {
            // 이미 생성된 기록 행의 표시 이름을 즉시 갱신하기 위한 임시 레코드 변수입니다.
            PlayerRaceRecordNet existingRecord = _raceRecords[existingIndex];
            existingRecord.displayName = new FixedString64Bytes(sanitizedName);
            _raceRecords[existingIndex] = existingRecord;
        }
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

    /// <summary>
    /// 로컬 클라이언트의 표시 이름을 서버 원본 기록 테이블에 반영합니다.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void SubmitDisplayNameRpc(FixedString64Bytes displayName, RpcParams rpcParams = default)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[StageProgress] SubmitDisplayName fallback 발생: called on non-server.");
            return;
        }

        // 이름을 제출한 플레이어의 클라이언트 식별자입니다.
        ulong sender = rpcParams.Receive.SenderClientId;
        if (!_recordIndexByClient.TryGetValue(sender, out int index))
        {
            AddRaceRecordIfMissing_Server(sender);
            if (!_recordIndexByClient.TryGetValue(sender, out index))
                return;
        }

        // RPC 입력값을 공통 정책으로 정규화한 표시 이름입니다.
        string sanitizedName = DisplayNamePolicy.Sanitize(displayName.ToString());
        _approvedDisplayNameByClient[sender] = sanitizedName;

        var record = _raceRecords[index];
        record.displayName = new FixedString64Bytes(sanitizedName);
        _raceRecords[index] = record;
    }

    /// <summary>
    /// 클라이언트 UI에서 사용할 레이스 기록 스냅샷을 반환합니다.
    /// 현재 진행 중 스테이지 Goal 도달 여부를 우선 반영해 정렬합니다.
    /// </summary>
    public bool TryGetRaceRecordsSnapshot(out List<PlayerRaceRecordSnapshot> orderedRecords)
    {
        _raceRecordSnapshotBuffer.Clear();
        if (_raceRecordSnapshotBuffer.Capacity < _raceRecords.Count)
            _raceRecordSnapshotBuffer.Capacity = _raceRecords.Count;

        for (int i = 0; i < _raceRecords.Count; i++)
        {
            var e = _raceRecords[i];
            _raceRecordSnapshotBuffer.Add(new PlayerRaceRecordSnapshot(e.clientId,
                e.displayName.ToString(), e.stage1Seconds, e.stage2Seconds, e.stage3Seconds));
        }

        // 현재 진행 중 스테이지 인덱스를 1~3 범위로 보정한 정렬 기준 값입니다.
        int sortStageIndex = Mathf.Clamp(CurrentStageIndex, 0, 2);

        // 재사용 비교기에 현재 스테이지 인덱스를 설정해 할당 없이 정렬합니다.
        _raceRecordSnapshotComparer.StageIndex = sortStageIndex;
        _raceRecordSnapshotBuffer.Sort(_raceRecordSnapshotComparer);

        orderedRecords = _raceRecordSnapshotBuffer;

        return true;
    }

    /// <summary>
    /// 점수표 정렬 시 스테이지 인덱스를 주입해 재사용 가능한 비교기입니다.
    /// </summary>
    private sealed class RaceRecordSnapshotComparer : IComparer<PlayerRaceRecordSnapshot>
    {
        /// <summary>
        /// 현재 정렬 기준으로 사용할 스테이지 인덱스입니다.
        /// </summary>
        public int StageIndex { get; set; }

        /// <summary>
        /// 두 레이스 기록 스냅샷의 우선순위를 비교합니다.
        /// </summary>
        public int Compare(PlayerRaceRecordSnapshot left, PlayerRaceRecordSnapshot right)
        {
            return CompareRaceRecordSnapshot(left, right, StageIndex);
        }
    }

    /// <summary>
    /// 점수표 정렬 규칙에 따라 두 플레이어 스냅샷의 우선순위를 비교합니다.
    /// </summary>
    private static int CompareRaceRecordSnapshot(PlayerRaceRecordSnapshot left, PlayerRaceRecordSnapshot right, int stageIndex)
    {
        // 왼쪽 플레이어의 현재 스테이지 Goal 도달 우선순위 값입니다.
        int leftGoalPriority = HasReachedStageGoal(left, stageIndex) ? 0 : 1;
        // 오른쪽 플레이어의 현재 스테이지 Goal 도달 우선순위 값입니다.
        int rightGoalPriority = HasReachedStageGoal(right, stageIndex) ? 0 : 1;
        if (leftGoalPriority != rightGoalPriority)
            return leftGoalPriority.CompareTo(rightGoalPriority);

        // 왼쪽 플레이어의 기록 보유 우선순위 값입니다.
        int leftRecordPriority = left.HasAnyStageRecorded ? 0 : 1;
        // 오른쪽 플레이어의 기록 보유 우선순위 값입니다.
        int rightRecordPriority = right.HasAnyStageRecorded ? 0 : 1;
        if (leftRecordPriority != rightRecordPriority)
            return leftRecordPriority.CompareTo(rightRecordPriority);

        // 왼쪽 플레이어의 Total 비교 기준 값입니다.
        float leftTotalForSort = left.HasAnyStageRecorded ? left.TotalSeconds : float.MaxValue;
        // 오른쪽 플레이어의 Total 비교 기준 값입니다.
        float rightTotalForSort = right.HasAnyStageRecorded ? right.TotalSeconds : float.MaxValue;
        int totalCompare = leftTotalForSort.CompareTo(rightTotalForSort);
        if (totalCompare != 0)
            return totalCompare;

        // 왼쪽 플레이어의 이름 비교 결과 값입니다.
        int displayNameCompare = string.Compare(left.DisplayName, right.DisplayName, System.StringComparison.Ordinal);
        if (displayNameCompare != 0)
            return displayNameCompare;

        return left.ClientId.CompareTo(right.ClientId);
    }

    /// <summary>
    /// 정렬 대상 플레이어가 특정 스테이지 Goal 기록을 보유했는지 확인합니다.
    /// </summary>
    private static bool HasReachedStageGoal(PlayerRaceRecordSnapshot snapshot, int stageIndex)
    {
        // 범위를 벗어난 stageIndex 입력에 대비해 보정한 스테이지 인덱스 값입니다.
        int normalizedStageIndex = Mathf.Clamp(stageIndex, 0, 2);

        // normalizedStageIndex에 대응하는 Goal 도달 여부 결과 변수입니다.
        bool hasReached = false;

        if (normalizedStageIndex == 0)
            hasReached = snapshot.HasStage1;
        else if (normalizedStageIndex == 1)
            hasReached = snapshot.HasStage2;
        else
            hasReached = snapshot.HasStage3;

        return hasReached;
    }

    /// <summary>
    /// 현재 로컬 플레이어가 특정 스테이지 기록을 확정했는지 확인합니다.
    /// </summary>
    public bool TryGetPlayerStageRecord(ulong clientId, int stageIndex, out float stageRecordSeconds)
    {
        stageRecordSeconds = MissingRecordValue;

        if (_recordIndexByClient.TryGetValue(clientId, out int idx))
        {
            var recordByIndex = _raceRecords[idx];
            stageRecordSeconds = GetStageRecordByIndex(recordByIndex, stageIndex);
            return stageRecordSeconds >= 0f;
        }

        for (int i = 0; i < _raceRecords.Count; i++)
        {
            if (_raceRecords[i].clientId != clientId)
                continue;

            stageRecordSeconds = GetStageRecordByIndex(_raceRecords[i], stageIndex);
            return stageRecordSeconds >= 0f;
        }

        return false;
    }

    /// <summary>
    /// 서버 연결 클라이언트 기준으로 기록 테이블을 재구성합니다.
    /// </summary>
    private void RebuildRaceRecordTable_Server()
    {
        _raceRecords.Clear();
        _recordIndexByClient.Clear();

        foreach (ulong id in NetworkManager.ConnectedClientsIds.OrderBy(v => v))
        {
            AddRaceRecordIfMissing_Server(id);
        }
    }

    /// <summary>
    /// 서버 기록 테이블에 플레이어 기본 행을 추가합니다.
    /// </summary>
    private void AddRaceRecordIfMissing_Server(ulong clientId)
    {
        if (_recordIndexByClient.ContainsKey(clientId))
            return;

        // 승인 시점 캐시에 저장된 표시 이름(없으면 기본 이름)을 선택한 문자열입니다.
        string initialDisplayName = ResolveInitialDisplayName(clientId);

        var entry = new PlayerRaceRecordNet
        {
            clientId = clientId,
            displayName = new FixedString64Bytes(initialDisplayName),
            stage1Seconds = MissingRecordValue,
            stage2Seconds = MissingRecordValue,
            stage3Seconds = MissingRecordValue,
        };

        _raceRecords.Add(entry);
        _recordIndexByClient[clientId] = _raceRecords.Count - 1;
    }

    /// <summary>
    /// 서버 기록 테이블에서 플레이어 행을 제거합니다.
    /// </summary>
    private void RemoveRaceRecord_Server(ulong clientId)
    {
        if (!_recordIndexByClient.TryGetValue(clientId, out int index))
            return;

        _raceRecords.RemoveAt(index);
        _recordIndexByClient.Clear();
        for (int i = 0; i < _raceRecords.Count; i++)
        {
            _recordIndexByClient[_raceRecords[i].clientId] = i;
        }
    }

    /// <summary>
    /// 서버 원본 도착 정보를 레이스 기록 테이블로 반영합니다.
    /// </summary>
    private void ApplyStageRecordToRaceTable_Server(ulong clientId, int stageIndex)
    {
        if (!_arrivalByStage.TryGetValue(stageIndex, out var arrivals))
            return;
        if (!arrivals.TryGetValue(clientId, out var arrivalInfo))
            return;

        AddRaceRecordIfMissing_Server(clientId);
        if (!_recordIndexByClient.TryGetValue(clientId, out int index))
            return;

        var record = _raceRecords[index];
        float stageSeconds = Mathf.Max(0f, (float)arrivalInfo.finalRecordSecondsFromStageStart);

        if (stageIndex == 0) record.stage1Seconds = stageSeconds;
        else if (stageIndex == 1) record.stage2Seconds = stageSeconds;
        else if (stageIndex == 2) record.stage3Seconds = stageSeconds;

        _raceRecords[index] = record;
    }

    /// <summary>
    /// 스테이지 인덱스에 맞는 기록값을 반환합니다.
    /// </summary>
    private float GetStageRecordByIndex(PlayerRaceRecordNet record, int stageIndex)
    {
        if (stageIndex == 0) return record.stage1Seconds;
        if (stageIndex == 1) return record.stage2Seconds;
        if (stageIndex == 2) return record.stage3Seconds;
        return MissingRecordValue;
    }

    /// <summary>
    /// 신규 기록 행 생성 시 사용할 초기 표시 이름을 결정합니다.
    /// </summary>
    private string ResolveInitialDisplayName(ulong clientId)
    {
        if (_approvedDisplayNameByClient.TryGetValue(clientId, out string cachedName))
            return DisplayNamePolicy.Sanitize(cachedName);

        // 서버(Host) 자신의 레코드 생성 시 NetworkConfig payload 이름을 우선 사용합니다.
        if (TryResolveDisplayNameFromNetworkConfigPayload_Server(clientId, out string payloadDisplayName))
            return payloadDisplayName;

        return BuildDefaultDisplayName(clientId);
    }

    /// <summary>
    /// NetworkManager 설정의 연결 payload에서 표시 이름을 복원합니다.
    /// </summary>
    private bool TryResolveDisplayNameFromNetworkConfigPayload_Server(ulong clientId, out string displayName)
    {
        displayName = string.Empty;

        if (!IsServer)
            return false;

        // 현재 서버 인스턴스의 NetworkManager 참조 변수입니다.
        NetworkManager networkManager = NetworkManager;
        if (networkManager == null || networkManager.NetworkConfig == null)
            return false;

        // Host 자신 레코드에만 NetworkConfig payload를 적용하기 위한 서버 클라이언트 식별자입니다.
        ulong serverClientId = NetworkManager.ServerClientId;
        if (clientId != serverClientId)
            return false;

        // NetworkConfig에 저장된 이름 payload 원본 바이트 배열입니다.
        byte[] payload = networkManager.NetworkConfig.ConnectionData;
        if (payload == null || payload.Length == 0)
            return false;

        // payload 디코딩 후 공통 정책으로 정규화한 표시 이름 문자열입니다.
        string sanitizedPayloadName = DisplayNamePolicy.ParseConnectionPayload(payload);
        if (string.IsNullOrWhiteSpace(sanitizedPayloadName))
            return false;

        displayName = sanitizedPayloadName;
        return true;
    }

    /// <summary>
    /// 표시 이름이 없을 때 사용할 기본 이름을 생성합니다.
    /// </summary>
    private string BuildDefaultDisplayName(ulong clientId)
    {
        // 다른 경로와 동일한 User 접두 기본 이름 생성을 위한 원본 문자열입니다.
        string fallbackRawName = $"User{clientId}";
        return DisplayNamePolicy.Sanitize(fallbackRawName);
    }
}
