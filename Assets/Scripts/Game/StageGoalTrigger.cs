using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public sealed class StageGoalTrigger : MonoBehaviour
{
    [SerializeField] private int _stageIndex;
    [SerializeField] private StageProgressController _stageProgress;
    [SerializeField] private bool _triggerOnce = true;

    private bool _reported;

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
}
