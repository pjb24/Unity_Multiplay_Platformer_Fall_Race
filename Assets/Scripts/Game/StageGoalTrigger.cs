using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public sealed class StageGoalTrigger : MonoBehaviour
{
    [SerializeField] private int _stageIndex;
    [SerializeField] private StageProgressController _stageProgress;
    [SerializeField] private bool _triggerOnce = true;

    private bool _reported;

    // 세션 상태 차단 로그를 1회만 출력하기 위한 플래그.
    private bool _hasLoggedSessionBlocked;

    private void Awake()
    {
        if (_stageProgress == null)
        {
            _stageProgress = FindFirstObjectByType<StageProgressController>();
            if (_stageProgress == null)
            {
                Debug.LogWarning("[StageGoalTrigger] Fallback 발생: StageProgressController not found in scene.");
            }
        }

        var col = GetComponent<Collider>();
        if (col != null && !col.isTrigger)
        {
            Debug.LogWarning("[StageGoalTrigger] Fallback 발생: Collider is not set as trigger.");
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (_triggerOnce && _reported) return;

        if (!IsRunningSessionInteractionAllowed())
            return;

        var netObject = other.GetComponentInParent<NetworkObject>();
        if (netObject == null || !netObject.IsOwner)
            return;

        if (_stageProgress == null)
        {
            Debug.LogWarning("[StageGoalTrigger] Report fallback 발생: StageProgressController missing.");
            return;
        }

        _stageProgress.ReportStageClearedRpc(_stageIndex);
        _reported = true;
    }

    /// <summary>
    /// 게임 세션이 Running 상태일 때만 골 상호작용을 허용한다.
    /// </summary>
    private bool IsRunningSessionInteractionAllowed()
    {
        // 현재 씬의 세션 상태를 제공하는 컨트롤러 참조.
        var sessionController = GameSessionController.Instance;
        if (sessionController == null)
        {
            if (!_hasLoggedSessionBlocked)
            {
                _hasLoggedSessionBlocked = true;
                Debug.LogWarning("[StageGoalTrigger] Session gate fallback 발생: GameSessionController is null. Block interaction.");
            }
            return false;
        }

        if (sessionController.State != E_GameSessionState.Running)
        {
            if (!_hasLoggedSessionBlocked)
            {
                _hasLoggedSessionBlocked = true;
                Debug.Log($"[StageGoalTrigger] Session gate 차단: state={sessionController.State}");
            }
            return false;
        }

        _hasLoggedSessionBlocked = false;
        return true;
    }
}
