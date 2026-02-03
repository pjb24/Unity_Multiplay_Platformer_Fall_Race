using Unity.Netcode;
using UnityEngine;

public sealed class ConnectionApprovalHandler : MonoBehaviour
{
    private void Awake()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        nm.ConnectionApprovalCallback += OnApproval;
    }

    private void OnDestroy()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        nm.ConnectionApprovalCallback -= OnApproval;
    }

    private void OnApproval(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved = true;
        response.CreatePlayerObject = true;
        response.Pending = false;
    }
}
