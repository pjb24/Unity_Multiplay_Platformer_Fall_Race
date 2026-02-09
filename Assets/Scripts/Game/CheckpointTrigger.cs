using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 체크포인트 트리거 (서버 권위).
/// </summary>
[RequireComponent(typeof(Collider))]
public sealed class CheckpointTrigger : MonoBehaviour
{
    [SerializeField] private int _checkpointIndex = 0;

    private void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServerAuthoritative()) return;

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
}
