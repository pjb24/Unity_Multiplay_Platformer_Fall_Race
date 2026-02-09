using Unity.Netcode;
using UnityEngine;

/// <summary>
/// FallZone 트리거 (서버 권위).
/// - OnTriggerEnter에서 Player 판별
/// - 서버에서만 추락 처리
/// </summary>
[RequireComponent(typeof(Collider))]
public sealed class FallZoneTrigger : MonoBehaviour
{
    private void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;

        int layer = LayerMask.NameToLayer("FallZone");
        if (layer >= 0)
            gameObject.layer = layer;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServerAuthoritative()) return;

        if (!other.CompareTag("Player"))
            return;

        var respawn = other.GetComponentInParent<PlayerRespawnServer>();
        if (respawn == null)
            return;

        respawn.TriggerRespawn_Server("fall_zone");
    }

    private static bool IsServerAuthoritative()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
            return true;

        return nm.IsServer;
    }
}
