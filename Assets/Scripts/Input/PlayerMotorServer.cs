using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 권위 Rigidbody 이동.
/// - 입력은 SetInput_Server()로 최신값을 갱신
/// - FixedUpdate에서만 물리 적용
/// - 이동 가능/불가 최종 결정은 서버에서 (Gate/Phase 기반)
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public sealed class PlayerMotorServer : NetworkBehaviour
{
    [Header("Move")]
    [SerializeField] private float _moveSpeed = 6f;
    [SerializeField] private float _airControl = 0.5f;

    [Header("Jump")]
    [SerializeField] private float _jumpVelocity = 6.5f;
    [SerializeField] private LayerMask _groundMask;
    [SerializeField] private float _groundCheckDistance = 1.15f;

    private Rigidbody _rb;

    private Vector2 _move;
    private bool _jumpDown;
    private int _tick;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            enabled = false;
            return;
        }
    }

    /// <summary>서버에서만 호출되어야 한다.</summary>
    public void SetInput_Server(Vector2 move, bool jumpDown, int tick)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerMotorServer] Fallback 발생: SetInput_Server called on non-server.");
            return;
        }

        // 오래된 입력 무시 (tick은 단조 증가 가정)
        if (tick <= _tick) return;

        _tick = tick;
        _move = Vector2.ClampMagnitude(move, 1f);

        // 덮어쓰기 금지: 점프 엣지 누적
        _jumpDown |= jumpDown;
    }

    /// <summary>서버에서 즉시 정지 처리(Goal 도달 등).</summary>
    public void StopImmediately_Server(string reason = null)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerMotorServer] Fallback 발생: StopImmediately_Server called on non-server.");
            return;
        }

        _move = Vector2.zero;
        _jumpDown = false;

        var v = _rb.linearVelocity;
        v.x = 0f;
        v.y = 0f;
        v.z = 0f;
        _rb.linearVelocity = v;
        _rb.angularVelocity = Vector3.zero;

        if (!string.IsNullOrEmpty(reason))
            Debug.Log($"[PlayerMotorServer] StopImmediately_Server: {reason}");
    }

    private void FixedUpdate()
    {
        if (!IsServer) return;

        // 최종 이동 가능 여부(서버 권위)
        if (!CanMove_Server())
        {
            // 즉시 정지(Goal/Countdown 등)
            StopImmediately_Server("gate_closed");
            return;
        }

        bool grounded = IsGrounded_Server();

        // 이동
        float control = grounded ? 1f : _airControl;
        Vector3 wish = new Vector3(_move.x, 0f, _move.y) * (_moveSpeed * control);

        Vector3 vCur = _rb.linearVelocity;
        vCur.x = wish.x;
        vCur.z = wish.z;
        _rb.linearVelocity = vCur;

        // 점프(1단)
        if (_jumpDown && grounded)
        {
            vCur = _rb.linearVelocity;
            vCur.y = _jumpVelocity;
            _rb.linearVelocity = vCur;
        }

        // 점프 엣지는 소비
        _jumpDown = false;
    }

    private bool CanMove_Server()
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerMotorServer] Fallback 발생: CanMove_Server called on non-server.");
            return false;
        }

        var session = GameSessionController.Instance;
        if (session == null)
        {
            Debug.LogWarning("[PlayerMotorServer] Fallback 발생: GameSessionController.Instance is null.");
            return false;
        }

        bool result = false;
        if (session.State == E_GameSessionState.Running)
        {
            result = true;
        }
        if (session.State == E_GameSessionState.Lobby)
        {
            result = true;
        }

        return result;
    }

    private bool IsGrounded_Server()
    {
        // 캡슐/콜라이더 구조에 따라 조정
        Vector3 origin = transform.position + Vector3.up * 0.1f;
        return Physics.Raycast(origin, Vector3.down, _groundCheckDistance, _groundMask, QueryTriggerInteraction.Ignore);
    }
}
