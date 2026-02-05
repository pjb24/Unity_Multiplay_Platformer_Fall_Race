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

        // 중복 구독 방지
        _nm.ConnectionApprovalCallback -= OnApproval;
        _nm.ConnectionApprovalCallback += OnApproval;
    }

    private void OnDisable()
    {
        if (_nm == null) return;
        _nm.ConnectionApprovalCallback -= OnApproval;
    }

    private void OnApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // 기본값 세팅 (NGO 권장 패턴)
        response.Approved = false;
        response.CreatePlayerObject = true;
        response.Pending = false;
        response.Reason = null;
        
        int max = (_quickSessionContext != null) ? _quickSessionContext.MaxPlayers : 0;

        if (_quickSessionContext == null || max <= 0)
        {
            Debug.LogWarning("[Netcode] ConnectionApproval fallback 발생: QuickSessionContext missing or invalid MaxPlayers. Reject.");
            response.Reason = "missing_quick_session_context";
            return;
        }

        // 현재 서버에 붙어있는 클라이언트 수 (Host 포함)
        int current = _nm.ConnectedClientsIds.Count;
        // 승인 조건: current < max
        if (current >= max)
        {
            Debug.LogWarning($"[Netcode] ConnectionApproval Reject: server_full. current={current}, max={max}");
            response.Reason = "server_full";
            return;
        }

        // 2) GameSession 상태 체크 (Game 씬에서만 유효)
        var session = GameSessionController.Instance;

        // ✅ 무음 폴백 금지: 상태 판단 불가면 Reject + Warning
        if (session == null)
        {
            Debug.LogWarning("[Approval] Reject: GameSessionController.Instance is null (cannot verify session state).");
            response.Reason = "session_missing";
            return;
        }

        // NetworkObject가 아직 스폰 안 된 타이밍 폴백
        if (!session.IsSpawned)
        {
            Debug.LogWarning("[Approval] Reject: GameSessionController not spawned yet (cannot verify session state).");
            response.Reason = "session_not_spawned";
            return;
        }

        // 서버가 아닌데 승인 콜백이 돈다면 구조가 이상함(일단 거부)
        if (!session.IsServer)
        {
            Debug.LogWarning("[Approval] Reject: approval callback invoked but GameSessionController is not server.");
            response.Reason = "not_server";
            return;
        }

        // ✅ 6) Countdown / Running 중 입장 차단
        if (session.State == E_GameSessionState.Countdown || session.State == E_GameSessionState.Running)
        {
            Debug.LogWarning($"[Approval] Reject: in_progress. state={session.State}");
            response.Reason = "in_progress";
            return;
        }

        // 3) Lobby 상태에서만 승인
        if (session.State != E_GameSessionState.Lobby)
        {
            Debug.LogWarning($"[Approval] Reject: not_in_lobby. state={session.State}");
            response.Reason = "not_in_lobby";
            return;
        }

        // 승인
        response.Approved = true;
    }
}
