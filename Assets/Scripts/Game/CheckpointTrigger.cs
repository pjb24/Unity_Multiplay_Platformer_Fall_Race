using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 체크포인트 트리거 (서버 권위).
/// </summary>
[RequireComponent(typeof(Collider))]
public sealed class CheckpointTrigger : MonoBehaviour
{
    [SerializeField] private int _checkpointIndex = 0;

    // 세션 상태 차단 로그를 1회만 출력하기 위한 플래그.
    private bool _hasLoggedSessionBlocked;

    private void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServerAuthoritative()) return;

        if (!IsRunningSessionInteractionAllowed())
            return;

        if (!other.CompareTag("Player"))
            return;

        var respawn = other.GetComponentInParent<PlayerRespawnServer>();
        if (respawn == null)
            return;

        respawn.SetCheckpoint_Server(transform.position, transform.rotation, _checkpointIndex);
    }

    private static bool IsServerAuthoritative()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
            return true;

        return nm.IsServer;
    }

    /// <summary>
    /// 게임 세션이 Running 상태일 때만 체크포인트 상호작용을 허용한다.
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
                Debug.LogWarning("[CheckpointTrigger] Session gate fallback 발생: GameSessionController is null. Block interaction.");
            }
            return false;
        }

        if (sessionController.State != E_GameSessionState.Running)
        {
            if (!_hasLoggedSessionBlocked)
            {
                _hasLoggedSessionBlocked = true;
                Debug.Log($"[CheckpointTrigger] Session gate 차단: state={sessionController.State}");
            }
            return false;
        }

        _hasLoggedSessionBlocked = false;
        return true;
    }
}
