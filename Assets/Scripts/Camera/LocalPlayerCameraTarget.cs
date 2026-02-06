using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Player(NetworkObject)가 스폰될 때 Owner면 카메라 타깃을 버스에 등록한다.
/// </summary>
public sealed class LocalPlayerCameraTarget : NetworkBehaviour
{
    [Header("Camera Target")]
    [SerializeField] private Transform _cameraTarget; // 없으면 자기 transform 사용

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
            return;

        var t = _cameraTarget != null ? _cameraTarget : transform;
        LocalPlayerTargetBus.Publish(t);
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
            return;

        var t = _cameraTarget != null ? _cameraTarget : transform;
        LocalPlayerTargetBus.Clear(t);
    }
}
