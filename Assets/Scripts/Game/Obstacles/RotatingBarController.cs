using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 서버 권위 Rotating Bar Zone.
/// - 회전 계산/피격 확정은 서버에서만 수행
/// - 클라이언트는 스냅샷 기반 보간으로 시각만 동기화
/// - 충돌 영역은 수학적 스윕 계산이 아니라 Unity Collider(Trigger/Collision)로 처리
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Collider))]
public sealed class RotatingBarController : NetworkBehaviour
{
    /// <summary>회전 방향.</summary>
    public enum E_Direction
    {
        CW = -1,
        CCW = 1,
    }

    /// <summary>넉백 방향 조합 모드.</summary>
    public enum E_KnockbackDirectionMode
    {
        RadialOnly = 0,
        RadialPlusTangential = 1,
    }

    /// <summary>
    /// 클라이언트 시각 보간용 스냅샷.
    /// 게임 판정은 항상 서버에서 별도 계산/충돌 확정한다.
    /// </summary>
    [Serializable]
    public struct RotatingBarSnapshot : INetworkSerializable, IEquatable<RotatingBarSnapshot>
    {
        public float AngleDeg;
        public float AngularVelocityDeg;
        public sbyte Direction;
        public double ServerTime;
        public uint Tick;

        /// <summary>네트워크 직렬화.</summary>
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref AngleDeg);
            serializer.SerializeValue(ref AngularVelocityDeg);
            serializer.SerializeValue(ref Direction);
            serializer.SerializeValue(ref ServerTime);
            serializer.SerializeValue(ref Tick);
        }

        /// <summary>스냅샷 값 동등성 비교.</summary>
        public bool Equals(RotatingBarSnapshot other)
        {
            return Mathf.Approximately(AngleDeg, other.AngleDeg)
                && Mathf.Approximately(AngularVelocityDeg, other.AngularVelocityDeg)
                && Direction == other.Direction
                && Math.Abs(ServerTime - other.ServerTime) <= 0.0001d
                && Tick == other.Tick;
        }
    }

    [Header("Rotation Spec")]
    // 회전 중심. 미지정 시 자기 자신(transform)을 피벗으로 사용한다.
    [SerializeField] private Transform _pivot;
    // 실제로 회전시킬 비주얼 오브젝트. 미지정 시 자기 자신(transform)을 회전한다.
    [SerializeField] private Transform _barVisual;
    [SerializeField] private bool _rotateAroundPivot = true;
    // 시작 각도(도). 서버 스폰 시 기준값으로 사용.
    [SerializeField, Range(0f, 360f)] private float _initialAngleDeg = 0f;
    // 기본 각속도(도/초).
    [SerializeField] private float _angularVelocityDeg = 120f;
    // 회전 방향(CW=-1, CCW=+1).
    [SerializeField] private E_Direction _direction = E_Direction.CCW;
    // true면 각가속도 항(0.5*a*t^2)을 회전식에 포함.
    [SerializeField] private bool _useAcceleration = false;
    // 각가속도(도/초^2). _useAcceleration=true일 때만 의미 있음.
    [SerializeField] private float _angularAccelerationDeg = 0f;

    [Header("Collider Hit")]
    [SerializeField] private Collider _hitCollider;
    [SerializeField] private bool _forceHitColliderAsTrigger = true;

    [Header("Hit Rules")]
    // 넉백 방향 계산 방식(방사/접선 혼합 여부).
    [SerializeField] private E_KnockbackDirectionMode _knockbackDirectionMode = E_KnockbackDirectionMode.RadialPlusTangential;
    // 넉백 최소 힘(Inspector 가이드 용도).
    [SerializeField] private float _knockbackForceMin = 7f;
    // 넉백 최대 힘(실제 적용값은 현재 구현에서 이 값 기준으로 계산).
    [SerializeField] private float _knockbackForceMax = 12f;
    // 수직 보정값. 피격 순간 땅에 붙어 미끄러지는 느낌을 줄이기 위해 위로 살짝 띄운다.
    [SerializeField] private float _knockbackVerticalBoost = 1.2f;
    // 피격 후 입력 잠금 시간(히트스턴 유사).
    [SerializeField] private float _knockbackControlLockSec = 0.25f;
    // 동일 플레이어 재피격 방지용 짧은 디바운스.
    [SerializeField] private float _hitDebounceSec = 0.2f;
    // 피격 후 짧은 무적 시간(i-frame).
    [SerializeField] private float _invincibleAfterHitSec = 0.3f;

    [Header("Sync")]
    // 스냅샷 최소 전송 간격.
    [SerializeField] private float _snapshotIntervalSec = 0.1f;
    // 이전 스냅샷 대비 각도 변화 임계치(이상 변화 시 즉시 전송).
    [SerializeField] private float _snapshotAngleThresholdDeg = 0.6f;

    [Header("Debug")]
    // 선택 시 충돌 구간 gizmo 표시.
    [SerializeField] private bool _drawCollisionGizmo = true;
    // 서버 tick/피격 로그 출력.
    [SerializeField] private bool _logServerAngleTick = false;
    [SerializeField] private bool _logColliderHit = false;

    /// <summary>
    /// 서버 확정 피격 이벤트.
    /// (playerId, hitPoint, normal, tick)
    /// </summary>
    [Header("Events")]
    [SerializeField] private UnityEvent _onAnyBarHit;

    public event Action<ulong, Vector3, Vector3, uint> OnBarHit;

    // 플레이어별 재피격 가능 시간(Time.time 기반).
    private readonly Dictionary<ulong, float> _nextAllowedHitAt = new();
    // 플레이어별 i-frame 종료 시각(Time.time 기반).
    private readonly Dictionary<ulong, float> _invincibleUntil = new();

    // 클라이언트 렌더 보간용 스냅샷. 게임 판정은 이 값을 쓰지 않고 서버 계산값을 사용한다.
    private readonly NetworkVariable<RotatingBarSnapshot> _snapshot = new(
        writePerm: NetworkVariableWritePermission.Server,
        readPerm: NetworkVariableReadPermission.Everyone);

    private double _serverStartTime;
    private float _lastServerAngleDeg;
    private float _nextSnapshotAt;
    private float _clientSmoothedAngle;
    private bool _isPaused;
    private float _speedMultiplier = 1f;

    private Vector3 _orbitOffsetAtZeroAngle;
    private float _orbitBaseYawDeg;
    private bool _orbitCached;

    /// <summary>
    /// 네트워크 스폰 시 초기 상태를 설정한다.
    /// - pivot/collider 참조 보정
    /// - 서버 기준 시각/초기 각도 설정
    /// - 클라 초기 보간 각도 설정
    /// </summary>
    public override void OnNetworkSpawn()
    {
        EnsureReferences();
        CacheOrbitData();
        ConfigureColliderForServerAuthority();

        if (IsServer)
        {
            // 절대시간 기준점 고정(서버 권위).
            _serverStartTime = NetworkManager.ServerTime.Time;
            _lastServerAngleDeg = NormalizeAngle(_initialAngleDeg);

            PublishSnapshot_Server(force: true);
            ApplyVisualAngle(_lastServerAngleDeg);
            return;
        }

        _clientSmoothedAngle = _snapshot.Value.AngleDeg;
        ApplyVisualAngle(_clientSmoothedAngle);
    }

    /// <summary>
    /// 인스펙터 값 변경 시 참조를 즉시 보정한다.
    /// </summary>
    private void OnValidate()
    {
        EnsureReferences();
        CacheOrbitData();
    }

    /// <summary>
    /// 프레임 루프.
    /// - 서버: 회전 계산 + 스냅샷 발행
    /// - 클라: 스냅샷 기반 시각 보간
    /// </summary>
    private void Update()
    {
        if (IsServer)
        {
            TickServerRotation();
            return;
        }

        TickClientVisual();
    }

    /// <summary>
    /// Trigger 진입 시 서버에서 플레이어 피격을 시도한다.
    /// </summary>
    private void OnTriggerEnter(Collider other)
    {
        TryHitPlayerByCollider_Server(other);
    }

    /// <summary>
    /// Trigger 유지 중 서버에서 플레이어 피격을 시도한다.
    /// (고핑/물리 업데이트 타이밍 이슈 완화)
    /// </summary>
    private void OnTriggerStay(Collider other)
    {
        TryHitPlayerByCollider_Server(other);
    }

    /// <summary>
    /// Non-trigger 충돌 진입 시 서버에서 플레이어 피격을 시도한다.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        TryHitPlayerByCollider_Server(collision.collider);
    }

    /// <summary>
    /// Non-trigger 충돌 유지 시 서버에서 플레이어 피격을 시도한다.
    /// </summary>
    private void OnCollisionStay(Collision collision)
    {
        TryHitPlayerByCollider_Server(collision.collider);
    }

    /// <summary>
    /// 서버 권위 회전 업데이트를 수행한다.
    /// </summary>
    private void TickServerRotation()
    {
        if (_isPaused)
            return;

        // 절대시간 기반 각도 계산: 누적 deltaTime 오차를 피한다.
        float current = ComputeAngleAtTime_Server(NetworkManager.ServerTime.Time);
        _lastServerAngleDeg = current;

        ApplyVisualAngle(current);
        PublishSnapshot_Server(force: false);

        if (_logServerAngleTick)
        {
            uint tick = (uint)NetworkManager.NetworkTickSystem.ServerTime.Tick;
            Debug.Log($"[RotatingBar] tick={tick}, angle={current:F2}, vel={_angularVelocityDeg:F2}, dir={(int)_direction}");
        }
    }

    /// <summary>
    /// 클라이언트 시각 보간을 수행한다.
    /// </summary>
    private void TickClientVisual()
    {
        RotatingBarSnapshot snap = _snapshot.Value;
        // 마지막 서버 스냅샷 시각 대비 경과시간으로 짧은 예측.
        float elapsed = (float)(NetworkManager.ServerTime.Time - snap.ServerTime);
        float predicted = NormalizeAngle(snap.AngleDeg + snap.AngularVelocityDeg * elapsed * snap.Direction);

        // 지수 보간으로 스냅샷 누락/점프를 완화.
        _clientSmoothedAngle = Mathf.LerpAngle(_clientSmoothedAngle, predicted, 1f - Mathf.Exp(-14f * Time.deltaTime));
        ApplyVisualAngle(_clientSmoothedAngle);
    }

    /// <summary>
    /// 회전 스냅샷을 주기/변화량 기준으로 전송한다.
    /// </summary>
    private void PublishSnapshot_Server(bool force)
    {
        float now = Time.time;
        // 과도한 브로드캐스트를 피하기 위해 최소 전송 간격을 둔다.
        if (!force && now < _nextSnapshotAt)
            return;

        uint tick = (uint)NetworkManager.NetworkTickSystem.ServerTime.Tick;
        double time = NetworkManager.ServerTime.Time;

        RotatingBarSnapshot next = new()
        {
            AngleDeg = _lastServerAngleDeg,
            AngularVelocityDeg = Mathf.Max(0f, _angularVelocityDeg) * Mathf.Max(0f, _speedMultiplier),
            Direction = (sbyte)_direction,
            ServerTime = time,
            Tick = tick,
        };

        // 각도 변화량 또는 값 변화가 충분할 때만 스냅샷을 업데이트.
        float angleGap = Mathf.Abs(Mathf.DeltaAngle(_snapshot.Value.AngleDeg, next.AngleDeg));
        if (force || angleGap >= _snapshotAngleThresholdDeg || !_snapshot.Value.Equals(next))
        {
            _snapshot.Value = next;
            _nextSnapshotAt = now + Mathf.Max(0.03f, _snapshotIntervalSec);
        }
    }

    /// <summary>
    /// 지정한 서버 시각의 바 각도를 계산한다.
    /// 필요 시 각가속도 항을 포함한 공식을 사용하고 결과는 0~360으로 정규화한다.
    /// </summary>
    private float ComputeAngleAtTime_Server(double serverTime)
    {
        double elapsed = Math.Max(0d, serverTime - _serverStartTime);
        float directionSign = (int)_direction;

        float baseVel = Mathf.Max(0f, _angularVelocityDeg) * Mathf.Max(0f, _speedMultiplier);
        float angle;
        if (_useAcceleration && Mathf.Abs(_angularAccelerationDeg) > 0.001f)
        {
            float accel = _angularAccelerationDeg * directionSign;
            // theta = theta0 + w*t + 1/2*a*t^2
            angle = _initialAngleDeg + directionSign * baseVel * (float)elapsed + 0.5f * accel * (float)(elapsed * elapsed);
        }
        else
        {
            angle = _initialAngleDeg + directionSign * baseVel * (float)elapsed;
        }

        return NormalizeAngle(angle);
    }

    /// <summary>
    /// 콜라이더 기반으로 플레이어 피격을 서버에서 확정한다.
    /// </summary>
    private void TryHitPlayerByCollider_Server(Collider other)
    {
        if (!IsServer || other == null)
            return;

        NetworkObject player = other.GetComponentInParent<NetworkObject>();
        if (player == null || !player.CompareTag("Player"))
            return;

        ulong playerId = player.OwnerClientId;
        if (!CanHitPlayerNow_Server(playerId))
            return;

        var respawn = player.GetComponent<PlayerRespawnServer>();
        if (respawn != null && respawn.IsRespawning)
            return;

        Vector3 hitPoint = GetApproxHitPoint(other);
        Vector3 normal = ResolveHitNormal(player.transform.position, hitPoint);
        Vector3 knockback = ComputeKnockbackVector(player.transform.position, normal);

        ApplyHit_Server(player, hitPoint, normal, knockback);
    }

    /// <summary>
    /// 피격 확정 결과를 서버에서 적용한다.
    /// - debounce/i-frame 갱신
    /// - 넉백 적용(PlayerMotorServer 또는 Rigidbody)
    /// - 이벤트/로그 송출
    /// </summary>
    private void ApplyHit_Server(NetworkObject player, Vector3 hitPoint, Vector3 normal, Vector3 knockback)
    {
        ulong playerId = player.OwnerClientId;
        // 재피격 제어 시각 갱신.
        _nextAllowedHitAt[playerId] = Time.time + Mathf.Max(0.01f, _hitDebounceSec);
        _invincibleUntil[playerId] = Time.time + Mathf.Max(0f, _invincibleAfterHitSec);

        var motor = player.GetComponent<PlayerMotorServer>();
        if (motor != null)
        {
            motor.ApplyKnockback_Server(knockback, _knockbackControlLockSec);
            // 회전 바 피격이 확정된 오너에게 충돌 SFX 재생을 요청합니다.
            motor.PlayObstacleHitSfx_Server();
        }
        else
        {
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null)
                rb.AddForce(knockback, ForceMode.Impulse);
        }

        uint tick = (uint)NetworkManager.NetworkTickSystem.ServerTime.Tick;
        OnBarHit?.Invoke(playerId, hitPoint, normal, tick);
        _onAnyBarHit?.Invoke();

        if (_logServerAngleTick || _logColliderHit)
        {
            Debug.Log($"[RotatingBar] tick={tick}, playerId={playerId}, angle={_lastServerAngleDeg:F2}, hitResult=Hit, knockbackVector={knockback}");
        }
    }

    /// <summary>
    /// 해당 플레이어가 현재 피격 가능한지(debounce/i-frame) 판정한다.
    /// </summary>
    private bool CanHitPlayerNow_Server(ulong playerId)
    {
        // debounce 또는 i-frame 중이면 이번 히트는 무시.
        if (_nextAllowedHitAt.TryGetValue(playerId, out float debounce) && Time.time < debounce)
            return false;

        if (_invincibleUntil.TryGetValue(playerId, out float invincible) && Time.time < invincible)
            return false;

        return true;
    }

    /// <summary>
    /// 넉백 벡터(방사 + 접선)를 계산한다.
    /// </summary>
    private Vector3 ComputeKnockbackVector(Vector3 playerCenter, Vector3 hitNormal)
    {
        // 방사 방향: pivot -> player
        Vector3 radial = playerCenter - _pivot.position;
        radial.y = 0f;
        if (radial.sqrMagnitude < 0.0001f)
            radial = hitNormal;
        radial.Normalize();

        // 접선 방향: 회전 방향을 반영한 수평 접선
        Vector3 tangent = Vector3.Cross(Vector3.up, radial) * (int)_direction;
        if (tangent.sqrMagnitude < 0.0001f)
            tangent = Vector3.right;
        tangent.Normalize();

        Vector3 combined = _knockbackDirectionMode == E_KnockbackDirectionMode.RadialOnly
            ? radial
            : (radial * 0.65f + tangent * 0.35f).normalized;

        // 현재는 최대힘 기반으로 충격량을 구성(향후 속도/충돌점 기반 스케일 확장 가능).
        float force = Mathf.Clamp(_knockbackForceMax, _knockbackForceMin, _knockbackForceMax);
        Vector3 impulse = combined * force;
        impulse.y = Mathf.Max(0f, _knockbackVerticalBoost);

        return impulse;
    }

    /// <summary>
    /// 비주얼의 회전을 지정 각도로 적용한다.
    /// - rotateAroundPivot=true: 지정 중심점(_pivot)을 기준으로 공전 + 회전
    /// - rotateAroundPivot=false: 기존처럼 제자리(y축) 회전
    /// </summary>
    private void ApplyVisualAngle(float angleDeg)
    {
        // 클라/서버 공통: 비주얼 회전만 적용(판정은 서버에서 별도 처리).
        Transform target = _barVisual != null ? _barVisual : transform;

        if (!_orbitCached)
            CacheOrbitData();

        if (_rotateAroundPivot)
        {
            Quaternion orbitRot = Quaternion.Euler(0f, angleDeg, 0f);
            target.position = _pivot.position + orbitRot * _orbitOffsetAtZeroAngle;

            Vector3 eulerOrbit = target.rotation.eulerAngles;
            eulerOrbit.y = _orbitBaseYawDeg + angleDeg;
            target.rotation = Quaternion.Euler(eulerOrbit);
            return;
        }

        Vector3 euler = target.rotation.eulerAngles;
        euler.y = angleDeg;
        target.rotation = Quaternion.Euler(euler);
    }

    /// <summary>
    /// 현재 pivot/비주얼 배치로부터 공전 기준 데이터를 캐시한다.
    /// 중심점을 바꿀 때 기준 위치를 재계산하기 위해 사용한다.
    /// </summary>
    private void CacheOrbitData()
    {
        EnsureReferences();

        Transform target = _barVisual != null ? _barVisual : transform;
        Vector3 offset = target.position - _pivot.position;
        if (offset.sqrMagnitude < 0.0001f)
            offset = Vector3.forward * 0.01f;

        Quaternion invInitial = Quaternion.Euler(0f, -NormalizeAngle(_initialAngleDeg), 0f);
        _orbitOffsetAtZeroAngle = invInitial * offset;
        _orbitBaseYawDeg = target.rotation.eulerAngles.y - NormalizeAngle(_initialAngleDeg);
        _orbitCached = true;
    }

    /// <summary>
    /// 충돌체에서 대략적인 히트 포인트를 추정한다.
    /// </summary>
    private Vector3 GetApproxHitPoint(Collider other)
    {
        Vector3 from = _pivot != null ? _pivot.position : transform.position;
        return other.ClosestPoint(from);
    }

    /// <summary>
    /// 피격 노말을 계산한다(0벡터 방지 포함).
    /// </summary>
    private Vector3 ResolveHitNormal(Vector3 playerPos, Vector3 hitPoint)
    {
        Vector3 normal = playerPos - hitPoint;
        normal.y = Mathf.Clamp(normal.y, -0.2f, 0.8f);

        if (normal.sqrMagnitude < 0.0001f)
            normal = playerPos - (_pivot != null ? _pivot.position : transform.position);
        if (normal.sqrMagnitude < 0.0001f)
            normal = Vector3.forward;

        return normal.normalized;
    }

    /// <summary>
    /// 필수 참조를 자동 보정한다.
    /// </summary>
    private void EnsureReferences()
    {
        if (_pivot == null)
            _pivot = transform;

        _orbitCached = false;

        if (_hitCollider == null)
        {
            if (_barVisual != null)
                _hitCollider = _barVisual.GetComponent<Collider>();

            if (_hitCollider == null)
                _hitCollider = GetComponent<Collider>();
        }
    }

    /// <summary>
    /// 서버 권위 충돌 처리를 위해 콜라이더 설정을 보정한다.
    /// </summary>
    private void ConfigureColliderForServerAuthority()
    {
        if (_hitCollider == null)
            return;

        if (_forceHitColliderAsTrigger)
            _hitCollider.isTrigger = true;

        // 움직이는 트리거 콜라이더 신뢰도를 높이기 위한 kinematic Rigidbody 보정.
        Rigidbody rb = _hitCollider.attachedRigidbody;
        if (rb == null)
            rb = _hitCollider.gameObject.GetComponent<Rigidbody>();

        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
        }
    }

    /// <summary>
    /// 임의 각도를 0~360 범위로 정규화한다.
    /// </summary>
    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle < 0f)
            angle += 360f;

        return angle;
    }

    /// <summary>
    /// 서버에서 회전을 일시정지한다(디버그/운영 제어용).
    /// </summary>
    [ContextMenu("Pause Rotation")]
    public void PauseRotation_Server()
    {
        if (!IsServer)
            return;

        // 디버그 운영용: 서버 회전을 일시정지.
        _isPaused = true;
    }

    /// <summary>
    /// 서버 회전을 재개한다.
    /// 현재 각도를 새 초기각으로 고정해 재개 시 각도 점프를 방지한다.
    /// </summary>
    [ContextMenu("Resume Rotation")]
    public void ResumeRotation_Server()
    {
        if (!IsServer)
            return;

        // 재개 시 현재 각도를 새 초기값으로 잡아 각도 점프를 방지.
        _isPaused = false;

        _initialAngleDeg = _lastServerAngleDeg;
        _serverStartTime = NetworkManager.ServerTime.Time;
    }

    /// <summary>
    /// 서버 회전 속도 배수를 변경한다.
    /// 각도 연속성을 유지하기 위해 현재 각도/시각을 기준점으로 재설정한다.
    /// </summary>
    public void SetSpeedMultiplier_Server(float multiplier)
    {
        if (!IsServer)
            return;

        // 속도 배수 변경 시에도 각도 연속성을 유지.
        _initialAngleDeg = _lastServerAngleDeg;
        _serverStartTime = NetworkManager.ServerTime.Time;

        _speedMultiplier = Mathf.Max(0f, multiplier);
        PublishSnapshot_Server(force: true);
    }

    /// <summary>
    /// Scene 뷰 디버그용으로 콜라이더 bounds/피벗을 그린다.
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (!_drawCollisionGizmo)
            return;

        EnsureReferences();

        if (_pivot != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(_pivot.position, 0.1f);
        }

        if (_hitCollider != null)
        {
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.25f);
            Bounds b = _hitCollider.bounds;
            Gizmos.DrawCube(b.center, b.size);
        }
    }
}
