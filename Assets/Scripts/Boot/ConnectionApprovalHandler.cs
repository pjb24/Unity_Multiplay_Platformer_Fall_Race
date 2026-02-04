using Unity.Netcode;
using UnityEngine;

public sealed class ConnectionApprovalHandler : MonoBehaviour
{
    [SerializeField] private QuickSessionContext _quickSessionContext;

    private NetworkManager _nm;

    private void Awake()
    {
        _nm = NetworkManager.Singleton;
        if (_nm == null)
        {
            Debug.LogError("[Netcode] NetworkManager.Singleton is null. NetworkRoot에 NetworkManager가 있어야 함.");
            return;
        }
    }

    private void OnEnable()
    {
        if (_nm == null) return;
        _nm.ConnectionApprovalCallback += OnApproval;
    }

    private void OnDisable()
    {
        if (_nm == null) return;
        _nm.ConnectionApprovalCallback -= OnApproval;
    }

    private void OnApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // 현재 서버에 붙어있는 클라이언트 수 (Host 포함)
        int current = _nm.ConnectedClientsIds.Count;

        // 승인 조건: current < max
        if (current >= _quickSessionContext.MaxPlayers)
        {
            Debug.LogWarning($"[Netcode] ConnectionApproval fallback 발생: max players reached. current={current}, max={_quickSessionContext.MaxPlayers}");
            response.Approved = false;
            response.Reason = "server_full";
            response.Pending = false; // 반드시 false로 끝내기
            return;
        }

        // 승인
        response.Approved = true;
        response.CreatePlayerObject = true;
        response.Pending = false;
    }
}
