using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

public sealed class RaceHudUIController : MonoBehaviour
{
    /// <summary>
    /// 현재 스테이지 진행 시간을 표시하는 텍스트입니다.
    /// </summary>
    [Header("Run Timer UI")]
    [SerializeField] private TMP_Text _txtStageTimer;

    /// <summary>
    /// 첫 도착 이후 제한시간 카운트다운을 표시하는 텍스트입니다.
    /// </summary>
    [Header("Goal Window UI")]
    [SerializeField] private TMP_Text _txtGoalWindowCountdown;

    /// <summary>
    /// 결과 테이블 루트 오브젝트입니다.
    /// </summary>
    [Header("Result Table UI")]
    [SerializeField] private GameObject _resultTableRoot;

    /// <summary>
    /// 결과 행을 생성할 부모 Transform입니다.
    /// </summary>
    [SerializeField] private Transform _rowParent;

    /// <summary>
    /// 결과 행 템플릿 프리팹입니다.
    /// </summary>
    [SerializeField] private RaceResultRowUI _rowTemplate;

    /// <summary>
    /// StageProgressController 캐시 참조입니다.
    /// </summary>
    private StageProgressController _stageProgress;

    /// <summary>
    /// 생성된 결과 행 캐시 목록입니다.
    /// </summary>
    private readonly List<RaceResultRowUI> _rows = new List<RaceResultRowUI>(8);

    /// <summary>
    /// 로컬 플레이어가 현재 스테이지 Goal을 확정했는지 여부입니다.
    /// </summary>
    private bool _isLocalStageResolved;

    /// <summary>
    /// 로컬 플레이어 현재 스테이지 확정 기록(초)입니다.
    /// </summary>
    private float _resolvedStageRecordSeconds = StageProgressController.MissingRecordValue;

    /// <summary>
    /// 마지막으로 처리한 세션 상태입니다.
    /// </summary>
    private E_GameSessionState _lastState = (E_GameSessionState)255;

    /// <summary>
    /// 한 라운드에서 Run 상태를 한 번이라도 시작했는지 추적하는 플래그입니다.
    /// </summary>
    private bool _hasRunStarted;

    private void Awake()
    {
        if (_rowTemplate != null)
            _rowTemplate.gameObject.SetActive(false);
    }

    /// <summary>
    /// HUD 초기화 및 표시 이름을 서버로 제출합니다.
    /// </summary>
    private void OnEnable()
    {
        _stageProgress = StageProgressController.Instance;
        SetResultVisible(false);
        TrySubmitDisplayNameToServer();
    }

    /// <summary>
    /// 매 프레임 서버 동기화 데이터 기반으로 HUD를 갱신합니다.
    /// </summary>
    private void Update()
    {
        var nm = NetworkManager.Singleton;
        var session = GameSessionController.Instance;
        if (nm == null || session == null)
            return;

        if (_stageProgress == null)
            _stageProgress = StageProgressController.Instance;
        if (_stageProgress == null)
            return;

        HandleStateTransition(session.State);
        RefreshStageTimerUI(nm, session);
        RefreshGoalWindowCountdownUI(nm, session);
        RefreshResultVisibilityByState(nm, session);
        RefreshResultTableUI(nm, session);
    }

    /// <summary>
    /// 세션 상태 전환 시 HUD 상태를 초기화합니다.
    /// </summary>
    private void HandleStateTransition(E_GameSessionState state)
    {
        if (_lastState == state)
            return;

        _lastState = state;

        if (state == E_GameSessionState.Countdown)
        {
            _isLocalStageResolved = false;
            _resolvedStageRecordSeconds = StageProgressController.MissingRecordValue;
            SetResultVisible(false);
        }

        if (state == E_GameSessionState.Running)
        {
            _hasRunStarted = true;
            SetResultVisible(false);
        }

        if (state == E_GameSessionState.Lobby)
        {
            _isLocalStageResolved = false;
            _resolvedStageRecordSeconds = StageProgressController.MissingRecordValue;
            _hasRunStarted = false;
            SetResultVisible(false);
        }
    }

    /// <summary>
    /// 현재 스테이지 진행 타이머를 표시하고 Goal 도착 시 고정합니다.
    /// </summary>
    private void RefreshStageTimerUI(NetworkManager nm, GameSessionController session)
    {
        if (_txtStageTimer == null)
            return;

        if (session.State != E_GameSessionState.Running)
        {
            if (!_isLocalStageResolved)
                _txtStageTimer.text = "";
            return;
        }

        // 현재 스테이지 인덱스입니다.
        int stageIndex = _stageProgress.CurrentStageIndex;
        // 로컬 클라이언트 식별자입니다.
        ulong localClientId = nm.LocalClientId;

        if (!_isLocalStageResolved)
        {
            if (_stageProgress.TryGetPlayerStageRecord(localClientId, stageIndex, out float fixedRecord))
            {
                _isLocalStageResolved = true;
                _resolvedStageRecordSeconds = fixedRecord;
            }
        }

        if (_isLocalStageResolved)
        {
            _txtStageTimer.text = $"Stage Time: {FormatTime(_resolvedStageRecordSeconds)}";
            return;
        }

        // 현재 서버 시작 시각입니다.
        double stageStartAt = _stageProgress.CurrentStageStartAtServerTime;
        float elapsed = Mathf.Max(0f, (float)(nm.ServerTime.Time - stageStartAt));
        _txtStageTimer.text = $"Stage Time: {FormatTime(elapsed)}";
    }

    /// <summary>
    /// 첫 도착 이후 미도착 플레이어에게만 제한시간을 표시합니다.
    /// </summary>
    private void RefreshGoalWindowCountdownUI(NetworkManager nm, GameSessionController session)
    {
        if (_txtGoalWindowCountdown == null)
            return;

        if (session.State != E_GameSessionState.Running)
        {
            _txtGoalWindowCountdown.text = "";
            return;
        }

        if (_isLocalStageResolved)
        {
            _txtGoalWindowCountdown.text = "";
            return;
        }

        // 현재 스테이지 목표 윈도우 종료 시각입니다.
        double endAt = _stageProgress.GoalWindowEndAtServerTime;
        if (endAt <= 0.0)
        {
            _txtGoalWindowCountdown.text = "";
            return;
        }

        float remain = Mathf.Max(0f, (float)(endAt - nm.ServerTime.Time));
        _txtGoalWindowCountdown.text = $"Goal Window: {FormatTime(remain)}";
    }

    /// <summary>
    /// 현재 세션 상태와 Goal 확정 상태를 기반으로 결과 UI 표시 여부를 제어합니다.
    /// </summary>
    private void RefreshResultVisibilityByState(NetworkManager nm, GameSessionController session)
    {
        // Run 시작 전에는 결과 UI를 항상 숨깁니다.
        if (!_hasRunStarted)
        {
            SetResultVisible(false);
            return;
        }

        // Running 상태에서는 결과 UI를 표시하지 않습니다.
        if (session.State == E_GameSessionState.Running)
        {
            SetResultVisible(false);
            return;
        }

        // Result 상태가 아니면 결과 UI를 표시하지 않습니다.
        if (session.State != E_GameSessionState.Result)
        {
            SetResultVisible(false);
            return;
        }

        // 로컬 플레이어가 Goal(Resolved) 상태인지 확인하기 위한 현재 스테이지 인덱스입니다.
        int stageIndex = _stageProgress.CurrentStageIndex;
        // 로컬 플레이어 클라이언트 식별자입니다.
        ulong localClientId = nm.LocalClientId;

        // Result 진입 시점에 최종 확정 기록을 다시 확인합니다.
        if (_stageProgress.TryGetPlayerStageRecord(localClientId, stageIndex, out float fixedRecord))
        {
            _isLocalStageResolved = true;
            _resolvedStageRecordSeconds = fixedRecord;
            SetResultVisible(true);
            return;
        }

        SetResultVisible(false);
    }

    /// <summary>
    /// 서버 원본 기록으로 결과 테이블을 구성하고 정렬 순서를 반영합니다.
    /// </summary>
    private void RefreshResultTableUI(NetworkManager nm, GameSessionController session)
    {
        if (_resultTableRoot == null || !_resultTableRoot.activeSelf)
            return;

        // Result 상태가 아니면 결과 테이블 데이터를 갱신하지 않습니다.
        if (session.State != E_GameSessionState.Result)
            return;

        // Run을 한 번도 시작하지 않았다면 결과 테이블을 갱신하지 않습니다.
        if (!_hasRunStarted)
            return;

        if (!_stageProgress.TryGetRaceRecordsSnapshot(out var records))
            return;

        EnsureRowCount(records.Count);

        // 동점 처리용 동일 Total 랭크 계산 캐시입니다.
        var rankByClient = BuildRankByClient(records);

        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var row = _rows[i];
            string stage1 = record.HasStage1 ? FormatTime(record.Stage1Seconds) : "-";
            string stage2 = record.HasStage2 ? FormatTime(record.Stage2Seconds) : "-";
            string stage3 = record.HasStage3 ? FormatTime(record.Stage3Seconds) : "-";
            string total = record.HasAnyStageRecorded ? FormatTime(record.TotalSeconds) : "-";
            string nameWithRank = rankByClient.TryGetValue(record.ClientId, out int rank)
                ? $"{rank}. {record.DisplayName}"
                : record.DisplayName;

            row.Bind(nameWithRank, stage1, stage2, stage3, total);
            row.gameObject.SetActive(true);
        }

        for (int i = records.Count; i < _rows.Count; i++)
            _rows[i].gameObject.SetActive(false);
    }

    /// <summary>
    /// 데이터 행 개수를 순위 데이터 수에 맞춰 맞춥니다.
    /// </summary>
    private void EnsureRowCount(int count)
    {
        if (_rowTemplate == null || _rowParent == null)
            return;

        while (_rows.Count < count)
        {
            var row = Instantiate(_rowTemplate, _rowParent);
            row.gameObject.SetActive(true);
            _rows.Add(row);
        }
    }

    /// <summary>
    /// 동점은 같은 등수를 부여하는 랭크 맵을 생성합니다.
    /// </summary>
    private Dictionary<ulong, int> BuildRankByClient(List<StageProgressController.PlayerRaceRecordSnapshot> records)
    {
        var map = new Dictionary<ulong, int>(records.Count);

        int currentRank = 0;
        int index = 0;
        float lastTotal = -1f;

        foreach (var r in records)
        {
            index++;
            if (!r.HasAnyStageRecorded)
            {
                map[r.ClientId] = 0;
                continue;
            }

            if (currentRank == 0 || !Mathf.Approximately(lastTotal, r.TotalSeconds))
            {
                currentRank = index;
                lastTotal = r.TotalSeconds;
            }

            map[r.ClientId] = currentRank;
        }

        return map;
    }

    /// <summary>
    /// 결과 테이블 표시 여부를 제어합니다.
    /// </summary>
    private void SetResultVisible(bool visible)
    {
        if (_resultTableRoot != null)
            _resultTableRoot.SetActive(visible);
    }

    /// <summary>
    /// 로컬 표시 이름을 서버에 1회 제출합니다.
    /// </summary>
    private void TrySubmitDisplayNameToServer()
    {
        if (_stageProgress == null)
            return;

        string displayName = $"Player {NetworkManager.Singleton?.LocalClientId ?? 0}";
        _stageProgress.SubmitDisplayNameRpc(new Unity.Collections.FixedString64Bytes(displayName));
    }

    /// <summary>
    /// 시간을 mm:ss.ff 형식으로 통일 변환합니다.
    /// </summary>
    private string FormatTime(float seconds)
    {
        float clamped = Mathf.Max(0f, seconds);
        int minutes = Mathf.FloorToInt(clamped / 60f);
        float sec = clamped - (minutes * 60f);
        return string.Format("{0:00}:{1:00.00}", minutes, sec);
    }
}
