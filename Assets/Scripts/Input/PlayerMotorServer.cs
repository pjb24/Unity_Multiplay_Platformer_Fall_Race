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

    [Header("Moving Platform")]
    [SerializeField] private float _jumpPlatformVelocityInherit = 0.85f;

    [Header("Knockback")]
    [SerializeField] private float _defaultKnockbackControlLockSec = 0.2f;

    [Header("Debug")]
    [SerializeField] private bool _logStopImmediately = false;

    private Rigidbody _rb;

    private Vector2 _move;
    private bool _jumpDown;
    private int _tick;
    private float _knockbackControlLockUntil;

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

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;

        if (_logStopImmediately && !string.IsNullOrEmpty(reason))
            Debug.Log($"[PlayerMotorServer] StopImmediately_Server: {reason}");
    }

    /// <summary>
    /// 서버 권위 넉백 적용.
    /// - Rigidbody에 Impulse를 가하고
    /// - 짧은 시간 이동 입력 오버라이드를 멈춰 튕겨나가는 느낌을 보존한다.
    /// </summary>
    public void ApplyKnockback_Server(Vector3 impulse, float controlLockSec = -1f)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerMotorServer] Fallback 발생: ApplyKnockback_Server called on non-server.");
            return;
        }

        _rb.AddForce(impulse, ForceMode.Impulse);

        float duration = controlLockSec >= 0f
            ? controlLockSec
            : Mathf.Max(0f, _defaultKnockbackControlLockSec);

        if (duration > 0f)
            _knockbackControlLockUntil = Mathf.Max(_knockbackControlLockUntil, Time.time + duration);
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

        bool grounded = TryGetGroundHit_Server(out RaycastHit hit);
        Vector3 platformVelocity = Vector3.zero;
        if (grounded)
        {
            var platform = hit.collider != null ? hit.collider.GetComponentInParent<MovingPlatformController>() : null;
            if (platform != null)
                platformVelocity = platform.CurrentVelocity;
        }

        // 이동
        float control = grounded ? 1f : _airControl;
        Vector3 wish = new Vector3(_move.x, 0f, _move.y) * (_moveSpeed * control);

        Vector3 vCur = _rb.linearVelocity;

        bool lockedByKnockback = Time.time < _knockbackControlLockUntil;
        if (!lockedByKnockback)
        {
            vCur.x = wish.x + platformVelocity.x;
            vCur.z = wish.z + platformVelocity.z;
            _rb.linearVelocity = vCur;
        }

        // 점프(1단)
        if (_jumpDown && grounded)
        {
            vCur = _rb.linearVelocity;
            vCur.y = _jumpVelocity;
            vCur.x += platformVelocity.x * Mathf.Clamp01(_jumpPlatformVelocityInherit);
            vCur.z += platformVelocity.z * Mathf.Clamp01(_jumpPlatformVelocityInherit);
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

    private bool TryGetGroundHit_Server(out RaycastHit hit)
    {
        // 캡슐/콜라이더 구조에 따라 조정
        Vector3 origin = transform.position + Vector3.up * 0.1f;
        return Physics.Raycast(origin, Vector3.down, out hit, _groundCheckDistance, _groundMask, QueryTriggerInteraction.Ignore);
    }
}
