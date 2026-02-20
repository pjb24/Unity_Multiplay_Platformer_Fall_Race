using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 권위 Rigidbody 이동.
/// - 입력은 SetInput_Server()로 최신값을 갱신
/// - FixedUpdate에서만 물리 적용
/// - 이동 가능/불가 최종 결정은 서버에서 (Gate/Phase 기반)
/// 
/// 서버 권위 이동 + 캐릭터 비주얼 배정 + 이동 기반 애니메이션 구동을 담당합니다.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public sealed class PlayerMotorServer : NetworkBehaviour
{
    private enum MotionAnimState
    {
        Idle,
        Running,
        Jump,
        Fall,
        Feeling,
    }

    [Header("Move")]
    /// <summary>달리기 기준 최대 이동 속도입니다.</summary>
    [SerializeField] private float _maxSpeed = 6f;
    /// <summary>입력이 있을 때 목표 속도로 접근하는 지상 가속도입니다.</summary>
    [SerializeField] private float _acceleration = 28f;
    /// <summary>입력이 없을 때 정지로 감속하는 지상 감속도입니다.</summary>
    [SerializeField] private float _deceleration = 34f;
    /// <summary>반대 방향으로 전환할 때 적용하는 가속도입니다.</summary>
    [SerializeField] private float _turnAcceleration = 38f;
    /// <summary>공중 상태에서 적용하는 기본 가속도입니다.</summary>
    [SerializeField] private float _airAcceleration = 12f;
    /// <summary>공중 상태에서 허용할 제어 비율(0~1)입니다.</summary>
    [SerializeField, Range(0f, 1f)] private float _airControl = 0.5f;
    /// <summary>달리기/걷기 분기 기준 입력 강도입니다.</summary>
    [SerializeField, Range(0f, 1f)] private float _runInputThreshold = 0.65f;
    /// <summary>걷기 상태에서 적용할 최대 속도 계수입니다.</summary>
    [SerializeField, Range(0.1f, 1f)] private float _walkSpeedMultiplier = 0.6f;
    /// <summary>미세 입력 노이즈 제거를 위한 데드존 값입니다.</summary>
    [SerializeField, Range(0f, 0.5f)] private float _inputDeadZone = 0.1f;
    /// <summary>정지 스냅에 사용하는 수평 속도 임계값입니다.</summary>
    [SerializeField] private float _stopThreshold = 0.05f;
    /// <summary>경사면 이동 허용 최대 각도입니다.</summary>
    [SerializeField, Range(0f, 89f)] private float _maxSlopeAngle = 50f;
    /// <summary>초당 회전 각도(도 단위)입니다.</summary>
    [SerializeField] private float _rotationSpeedDegPerSec = 540f;
    /// <summary>회전을 시작할 최소 이동 입력 제곱 크기 임계값입니다.</summary>
    [SerializeField] private float _rotationThreshold = 0.001f;

    [Header("Jump")]
    /// <summary>점프 시 설정할 수직 속도입니다.</summary>
    [SerializeField] private float _jumpVelocity = 6.5f;
    /// <summary>Ground 판정 대상 레이어 마스크입니다(플랫폼 포함 필수).</summary>
    [SerializeField] private LayerMask _groundMask;
    /// <summary>하향 Raycast 기본 길이입니다.</summary>
    [SerializeField] private float _groundCheckDistance = 1.15f;
    /// <summary>하강 속도에 비례해 Ground 체크 길이에 더할 추가 거리 계수입니다.</summary>
    [SerializeField] private float _groundCheckFallDistancePerSpeed = 0.12f;

    [Header("Head Check")]
    /// <summary>상단 충돌(천장 압착) 감지용 레이어 마스크입니다.</summary>
    [SerializeField] private LayerMask _headCheckMask;
    /// <summary>상향 Raycast 길이입니다.</summary>
    [SerializeField] private float _headCheckDistance = 1.05f;

    [Header("Moving Platform")]
    /// <summary>점프 순간 플랫폼 수평 속도 상속 비율입니다.</summary>
    [SerializeField] private float _jumpPlatformVelocityInherit = 0.85f;
    /// <summary>플랫폼 상승 중 점프 시 플랫폼 y속도 상속 비율입니다.</summary>
    [SerializeField] private float _jumpPlatformUpwardVelocityInherit = 1f;

    [Header("Knockback")]
    [SerializeField] private float _defaultKnockbackControlLockSec = 0.2f;

    [Header("Animation Threshold")]
    /// <summary>달리기 상태로 판단할 최소 수평 속도 임계값입니다.</summary>
    [SerializeField] private float _runSpeedThreshold = 0.15f;
    /// <summary>점프 후 낙하 상태로 전환하는 최소 낙하 거리 임계값입니다.</summary>
    [SerializeField] private float _fallDistanceFromLiftOffThreshold = 0.15f;

    [Header("Animation Param Names")]
    [SerializeField] private string _idleParam = "idle";
    [SerializeField] private string _runParam = "run";
    [SerializeField] private string _fallParam = "fall";
    [SerializeField] private string _feelParam = "feel";
    [SerializeField] private string _jumpParam = "jump";
    [SerializeField] private string _feelBlendParam = "blend feeling";

    [Header("Visual")]
    [SerializeField] private Transform _visualRoot;

    [Header("Sfx")]
    /// <summary>플레이어 이동/점프 사운드를 제어하는 컴포넌트 참조입니다.</summary>
    [SerializeField] private PlayerSfxController _playerSfxController;

    [Header("Debug")]
    [SerializeField] private bool _logStopImmediately = false;

    /// <summary>물리 이동 처리에 사용하는 Rigidbody 참조입니다.</summary>
    private Rigidbody _rb;

    /// <summary>서버가 수신한 최신 이동 입력 벡터입니다.</summary>
    private Vector2 _move;
    /// <summary>이동 입력에서 분리한 정규화 방향 벡터입니다.</summary>
    private Vector3 _inputDirection;
    /// <summary>이동 입력에서 분리한 강도(0~1) 값입니다.</summary>
    private float _inputStrength;
    /// <summary>서버가 수신한 점프 입력 래치입니다.</summary>
    private bool _jumpDown;
    /// <summary>입력 역행 방지용 tick 값입니다.</summary>
    private int _tick;
    /// <summary>넉백 후 입력 잠금 해제 시각입니다.</summary>
    private float _knockbackControlLockUntil;
    /// <summary>현재 접지 중인 플랫폼 참조(서버 판정)입니다.</summary>
    private MovingPlatformController _currentPlatform;

    /// <summary>현재 표시 중인 캐릭터 비주얼 인스턴스입니다.</summary>
    private GameObject _activeVisual;
    /// <summary>현재 캐릭터의 Animator 참조입니다.</summary>
    private Animator _animator;

    /// <summary>서버가 확정한 캐릭터 ID를 모든 클라이언트에 동기화하는 단일 상태값입니다.</summary>
    private readonly NetworkVariable<FixedString64Bytes> _networkCharacterId = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    /// <summary>서버가 확정한 fallback 사용 여부를 동기화하는 상태값입니다.</summary>
    private readonly NetworkVariable<bool> _networkCharacterIsFallback = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    /// <summary>중복 생성 방지를 위한 마지막 적용 캐릭터 ID 캐시입니다.</summary>
    private string _lastAppliedCharacterId = string.Empty;
    /// <summary>중복 생성 방지를 위한 마지막 적용 fallback 캐시입니다.</summary>
    private bool _lastAppliedFallback = true;

    /// <summary>발이 지면에서 떨어진 순간의 y 좌표입니다.</summary>
    private float _liftOffY;
    /// <summary>공중 상태 여부(점프/낙하 판정 기준)입니다.</summary>
    private bool _wasAirborne;
    /// <summary>낙하 상태 여부입니다.</summary>
    private bool _isFalling;
    /// <summary>fall 진입 후 착지 전까지 run 트리거를 막기 위한 잠금 플래그입니다.</summary>
    private bool _fallStateLockedUntilGrounded;
    /// <summary>애니메이션 상태 전환 중복 방지 캐시입니다.</summary>
    private MotionAnimState _lastAnimState = MotionAnimState.Idle;
    /// <summary>이번 프레임에 착지했는지 추적하는 플래그입니다.</summary>
    private bool _landedThisFrame;

    /// <summary>서버에서 결과 연출 활성화를 동기화하는 플래그입니다.</summary>
    private readonly NetworkVariable<bool> _resultFeelActive = new NetworkVariable<bool>(false);
    /// <summary>서버에서 결과 연출 win/lose 블렌드 값을 동기화하는 변수입니다.</summary>
    private readonly NetworkVariable<float> _resultFeelBlend = new NetworkVariable<float>(0f);
    /// <summary>GameSession 이벤트 구독 완료 여부를 추적하는 플래그입니다.</summary>
    private bool _characterAssignmentEventsSubscribed;

    private void Awake()
    {
        // 플레이어 이동 제어에 사용하는 Rigidbody를 캐싱합니다.
        _rb = GetComponent<Rigidbody>();
        // 고속 플랫폼 접촉 시 관통/미검출을 줄이기 위해 연속 충돌을 사용합니다.
        _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        // 원격 동기화 시 시각적 떨림 완화를 위해 보간을 사용합니다.
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        // 비주얼 부모가 지정되지 않으면 자기 자신을 사용합니다.
        if (_visualRoot == null)
            _visualRoot = transform;

        // SFX 컨트롤러가 누락된 경우 동일 오브젝트에서 자동으로 탐색합니다.
        if (_playerSfxController == null)
            _playerSfxController = GetComponent<PlayerSfxController>();

        // 발이 지면에서 떨어지는 시점 기준값 초기화입니다.
        _liftOffY = transform.position.y;
        _wasAirborne = false;
        _fallStateLockedUntilGrounded = false;
    }

    public override void OnNetworkSpawn()
    {
        // 캐릭터 동기화 상태 변경을 구독합니다.
        _networkCharacterId.OnValueChanged += OnNetworkCharacterStateChanged;
        _networkCharacterIsFallback.OnValueChanged += OnNetworkCharacterFallbackChanged;

        // 결과 연출 동기화 값 변화를 구독합니다.
        _resultFeelActive.OnValueChanged += OnResultFeelingChanged;
        _resultFeelBlend.OnValueChanged += OnResultFeelingBlendChanged;

        // 캐릭터 배정 변경 이벤트를 구독합니다.
        SubscribeCharacterAssignmentEvents();
        // 이미 배정된 캐릭터가 있으면 즉시 비주얼을 구성합니다.
        TryApplyAssignedCharacterVisual();

        Debug.Log($"[CharacterSync] OnNetworkSpawn owner={OwnerClientId}, local={NetworkManager.LocalClientId}, isServer={IsServer}, netCharacterId='{_networkCharacterId.Value}'");
    }

    public override void OnNetworkDespawn()
    {
        _networkCharacterId.OnValueChanged -= OnNetworkCharacterStateChanged;
        _networkCharacterIsFallback.OnValueChanged -= OnNetworkCharacterFallbackChanged;

        _resultFeelActive.OnValueChanged -= OnResultFeelingChanged;
        _resultFeelBlend.OnValueChanged -= OnResultFeelingBlendChanged;
        UnsubscribeCharacterAssignmentEvents();
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
        BuildNormalizedInput(_move, out _inputDirection, out _inputStrength);

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

    /// <summary>
    /// 서버에서 결과 애니메이션(feel + blend feeling)을 설정합니다.
    /// </summary>
    public void SetResultFeeling_Server(float blendFeel)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerMotorServer] Fallback 발생: SetResultFeeling_Server called on non-server.");
            return;
        }

        _resultFeelBlend.Value = Mathf.Clamp01(blendFeel);
        _resultFeelActive.Value = true;
    }

    /// <summary>
    /// 서버에서 결과 애니메이션 상태를 해제합니다.
    /// </summary>
    public void ClearResultFeeling_Server()
    {
        if (!IsServer)
            return;

        _resultFeelActive.Value = false;
        _resultFeelBlend.Value = 0f;

        // 서버 인스턴스에서 즉시 feel 잔여 트리거를 정리해 다음 상태 전이를 안정화합니다.
        HandleResultFeelingCleared();
    }

    /// <summary>
    /// 리스폰 직후 공중/낙하 판정과 트리거 캐시를 초기화해 정상적인 이동 애니메이션 전환을 보장합니다.
    /// </summary>
    public void ResetMotionState_Server()
    {
        if (!IsServer)
            return;

        _wasAirborne = false;
        _isFalling = false;
        _fallStateLockedUntilGrounded = false;
        _landedThisFrame = false;
        _liftOffY = transform.position.y;
        _lastAnimState = MotionAnimState.Idle;

        if (_animator == null)
            return;

        _animator.ResetTrigger(_jumpParam);
        _animator.ResetTrigger(_fallParam);
        _animator.ResetTrigger(_feelParam);
        _animator.SetTrigger(_idleParam);
    }

    private void Update()
    {
        // GameSessionController 지연 생성 상황에서도 이벤트 구독이 성립되도록 매 프레임 확인합니다.
        EnsureCharacterAssignmentEventSubscription();

        // 비주얼이 아직 준비되지 않았다면 계속 재시도합니다.
        if (_activeVisual == null)
            TryApplyAssignedCharacterVisual();

        // 서버 권한 인스턴스는 세션 배정값을 네트워크 상태값에 반영합니다.
        if (IsServer && _networkCharacterId.Value.Length == 0)
            TrySyncCharacterStateFromSession_Server();

        UpdateMotionAnimation();
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

        // 이동 계산 전 접지/충돌 상태를 먼저 갱신합니다.
        bool grounded = TryGetGroundHit(out RaycastHit hit);
        // 점프 순간 수평 속도 상속에 사용할 플랫폼 속도입니다.
        Vector3 platformVelocity = Vector3.zero;
        if (grounded && TryResolveGroundedPlatform(hit, out MovingPlatformController platform))
        {
            _currentPlatform = platform;
            platformVelocity = platform.CurrentVelocity;
        }
        else
        {
            _currentPlatform = null;
        }

        // 이동
        float stateSpeedMultiplier = _inputStrength >= _runInputThreshold ? 1f : _walkSpeedMultiplier;
        float maxStateSpeed = _maxSpeed * stateSpeedMultiplier;
        float controlRatio = grounded ? 1f : Mathf.Clamp01(_airControl);

        // 플랫폼 기준 상대 이동을 만들기 위한 수평 플랫폼 속도입니다.
        Vector3 platformHorizontalVelocity = new Vector3(platformVelocity.x, 0f, platformVelocity.z);
        Vector3 movePlaneNormal = GetMovePlaneNormal(grounded, hit);
        // 입력으로 만들어지는 목표 상대 수평 속도입니다.
        Vector3 targetRelativeHorizontalVelocity = Vector3.ProjectOnPlane(_inputDirection * (_inputStrength * maxStateSpeed), movePlaneNormal);
        targetRelativeHorizontalVelocity *= controlRatio;

        Vector3 currentVelocity = _rb.linearVelocity;
        // 현재 월드 수평 속도입니다.
        Vector3 currentHorizontalVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
        // 현재 플랫폼 기준 상대 수평 속도입니다.
        Vector3 currentRelativeHorizontalVelocity = grounded ? currentHorizontalVelocity - platformHorizontalVelocity : currentHorizontalVelocity;
        float accelerationRate = GetAccelerationRate(grounded, currentRelativeHorizontalVelocity, targetRelativeHorizontalVelocity);
        // 플랫폼 기준 상대 수평 속도를 입력 목표값으로 수렴시킵니다.
        Vector3 nextRelativeHorizontalVelocity = Vector3.MoveTowards(
            currentRelativeHorizontalVelocity,
            targetRelativeHorizontalVelocity,
            accelerationRate * Time.fixedDeltaTime);

        float maxHorizontalMagnitude = grounded ? maxStateSpeed : maxStateSpeed * controlRatio;
        nextRelativeHorizontalVelocity = Vector3.ClampMagnitude(nextRelativeHorizontalVelocity, maxHorizontalMagnitude);
        Vector3 nextHorizontalVelocity = grounded
            ? nextRelativeHorizontalVelocity + platformHorizontalVelocity
            : nextRelativeHorizontalVelocity;
        if (_inputStrength <= 0f && nextRelativeHorizontalVelocity.magnitude <= _stopThreshold)
        {
            nextRelativeHorizontalVelocity = Vector3.zero;
            nextHorizontalVelocity = grounded ? platformHorizontalVelocity : Vector3.zero;
        }

        // 플랫폼 이동 영향이 아닌 플레이어 상대 이동 기준으로 캐릭터 회전을 갱신합니다.
        Vector3 rotationVelocity = grounded ? nextRelativeHorizontalVelocity : nextHorizontalVelocity;
        RotateCharacter_Server(rotationVelocity);

        bool lockedByKnockback = Time.time < _knockbackControlLockUntil;
        if (!lockedByKnockback)
        {
            // XZ 수평 이동만 갱신하고 Y 수직 속도는 보존합니다.
            currentVelocity.x = nextHorizontalVelocity.x;
            currentVelocity.z = nextHorizontalVelocity.z;
            _rb.linearVelocity = currentVelocity;
        }

        // 점프(1단)
        if (_jumpDown && grounded)
        {
            currentVelocity = _rb.linearVelocity;
            // 점프 순간 y속도를 명시적으로 재설정해 중복 가속 상속을 방지합니다.
            currentVelocity.y = _jumpVelocity;
            // 상승 중인 플랫폼 위 점프는 플랫폼의 상향 속도를 추가해 체감 점프 높이를 유지합니다.
            float inheritedUpwardVelocity = Mathf.Max(0f, platformVelocity.y) * Mathf.Clamp01(_jumpPlatformUpwardVelocityInherit);
            currentVelocity.y += inheritedUpwardVelocity;
            currentVelocity.x += platformVelocity.x * Mathf.Clamp01(_jumpPlatformVelocityInherit);
            currentVelocity.z += platformVelocity.z * Mathf.Clamp01(_jumpPlatformVelocityInherit);
            _rb.linearVelocity = currentVelocity;
            _currentPlatform = null;
        }

        if (HasCeilingAbove())
        {
            currentVelocity = _rb.linearVelocity;
            if (currentVelocity.y > 0f)
            {
                currentVelocity.y = 0f;
                _rb.linearVelocity = currentVelocity;
            }
        }

        // 이동 계산 후 접지 상태를 한 번 더 갱신해 연산 순서를 고정합니다.
        TryGetGroundHit(out _);

        // 점프 엣지는 소비
        _jumpDown = false;
    }

    /// <summary>
    /// 이동/물리 상태 우선순위(feel > fall > jump > run > idle)로 애니메이션 트리거를 발행합니다.
    /// </summary>
    private void UpdateMotionAnimation()
    {
        if (_animator == null)
            return;

        if (_resultFeelActive.Value)
        {
            UpdateMovementSfxState(false, 0f, false);
            TriggerAnimatorState(MotionAnimState.Feeling);
            return;
        }

        bool grounded = TryGetGroundHit(out _);
        float currentY = transform.position.y;

        if (!grounded)
        {
            _landedThisFrame = false;

            if (!_wasAirborne)
            {
                // 발이 지면에서 떨어진 첫 프레임의 y를 기록합니다.
                _liftOffY = currentY;
                _wasAirborne = true;
            }

            // 이륙 시점 대비 얼마나 아래로 내려왔는지로 낙하를 판정합니다.
            float dropFromLiftOff = _liftOffY - currentY;
            bool shouldFall = dropFromLiftOff >= _fallDistanceFromLiftOffThreshold;

            if (_fallStateLockedUntilGrounded)
            {
                _isFalling = true;
                TriggerAnimatorState(MotionAnimState.Fall);
                return;
            }

            _isFalling = shouldFall;
            if (shouldFall)
                _fallStateLockedUntilGrounded = true;

            // 비오너 클라이언트는 입력값을 가지지 않으므로 현재 속도로 이동 상태를 보정합니다.
            bool hasMoveInputWhileAirborne = HasMovementForSfx();
            UpdateMovementSfxState(false, 0f, hasMoveInputWhileAirborne);
            TriggerAnimatorState(shouldFall ? MotionAnimState.Fall : MotionAnimState.Jump);
            return;
        }

        _landedThisFrame = _wasAirborne || _fallStateLockedUntilGrounded;

        if (_fallStateLockedUntilGrounded)
        {
            _isFalling = false;
            _wasAirborne = false;
            _fallStateLockedUntilGrounded = false;
            return;
        }

        _isFalling = false;
        _wasAirborne = false;
        Vector3 horizontalVelocity = _rb != null ? new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z) : Vector3.zero;

        // 오너 입력값이 없는 비오너 클라이언트에서도 발소리가 재생되도록 속도 기반 보정을 포함합니다.
        bool hasMoveInput = HasMovementForSfx();
        float runThreshold = Mathf.Max(_stopThreshold, _runSpeedThreshold);
        // 실제 이동 속도 기준으로만 run 상태를 판정해 방향 전환 시 run 재트리거가 가능하도록 합니다.
        bool isRunning = horizontalVelocity.magnitude >= runThreshold;

        // 착지 직후 이동 입력이 유지되고 있으면 즉시 run 전환을 허용합니다.
        if (_landedThisFrame && hasMoveInput)
            isRunning = true;

        UpdateMovementSfxState(true, horizontalVelocity.magnitude, hasMoveInput);
        TriggerAnimatorState(isRunning ? MotionAnimState.Running : MotionAnimState.Idle);
    }

    /// <summary>
    /// SFX 판정용 이동 여부를 입력 또는 실제 수평 속도 기준으로 계산합니다.
    /// </summary>
    private bool HasMovementForSfx()
    {
        // 오너 인스턴스에서 수신한 이동 입력 강도 기반 이동 여부입니다.
        bool hasInputMovement = _inputStrength > 0f;
        if (hasInputMovement)
            return true;

        // 네트워크 동기화된 Rigidbody 수평 속도 기반 이동 여부입니다.
        Vector3 horizontalVelocity = _rb != null
            ? new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z)
            : Vector3.zero;
        return horizontalVelocity.magnitude > _stopThreshold;
    }

    /// <summary>
    /// 이동 입력이 있는 경우에만 목표 방향으로 캐릭터를 회전시킵니다.
    /// </summary>
    private void RotateCharacter_Server(Vector3 moveDirection)
    {
        if (!IsServer)
            return;

        // 미세한 입력 노이즈로 회전이 흔들리는 것을 막습니다.
        if (moveDirection.sqrMagnitude < _rotationThreshold)
            return;

        Vector3 flattenedDirection = moveDirection.normalized;
        Quaternion targetRotation = Quaternion.LookRotation(flattenedDirection, Vector3.up);
        Quaternion nextRotation = Quaternion.RotateTowards(
            _rb.rotation,
            targetRotation,
            _rotationSpeedDegPerSec * Time.fixedDeltaTime);

        _rb.MoveRotation(nextRotation);
    }

    /// <summary>
    /// 입력 벡터를 방향과 강도로 분리하고 데드존을 적용합니다.
    /// </summary>
    private void BuildNormalizedInput(Vector2 moveInput, out Vector3 inputDirection, out float inputStrength)
    {
        float magnitude = moveInput.magnitude;
        if (magnitude < _inputDeadZone)
        {
            inputDirection = Vector3.zero;
            inputStrength = 0f;
            return;
        }

        Vector2 normalizedInput = moveInput / Mathf.Max(0.0001f, magnitude);
        inputDirection = new Vector3(normalizedInput.x, 0f, normalizedInput.y);
        inputStrength = Mathf.Clamp01(magnitude);
    }

    /// <summary>
    /// 접지 상태/방향 전환 여부에 따라 적용할 가속도를 계산합니다.
    /// </summary>
    private float GetAccelerationRate(bool grounded, Vector3 currentHorizontalVelocity, Vector3 targetHorizontalVelocity)
    {
        if (targetHorizontalVelocity.sqrMagnitude <= 0.000001f)
            return grounded ? _deceleration : _airAcceleration;

        bool isTurning = currentHorizontalVelocity.sqrMagnitude > 0.000001f
            && Vector3.Dot(currentHorizontalVelocity.normalized, targetHorizontalVelocity.normalized) < 0f;

        if (!grounded)
            return _airAcceleration;

        return isTurning ? _turnAcceleration : _acceleration;
    }

    /// <summary>
    /// 경사면에서는 접지 노멀을, 나머지는 월드 업 축을 이동 평면 노멀로 사용합니다.
    /// </summary>
    private Vector3 GetMovePlaneNormal(bool grounded, RaycastHit hit)
    {
        if (!grounded)
            return Vector3.up;

        float slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
        if (slopeAngle <= _maxSlopeAngle)
            return hit.normal;

        return Vector3.up;
    }

    /// <summary>
    /// 애니메이션 상태별 트리거를 중복 없이 발행합니다.
    /// </summary>
    private void TriggerAnimatorState(MotionAnimState nextState)
    {
        if (_animator == null)
            return;

        if (_lastAnimState == nextState)
            return;

        switch (nextState)
        {
            case MotionAnimState.Idle:
                _animator.SetTrigger(_idleParam);
                break;
            case MotionAnimState.Running:
                _animator.SetTrigger(_runParam);
                break;
            case MotionAnimState.Jump:
                _animator.SetTrigger(_jumpParam);
                PlayJumpSfx();
                break;
            case MotionAnimState.Fall:
                _animator.SetTrigger(_fallParam);
                break;
            case MotionAnimState.Feeling:
                // 결과 연출 시작 직전에 적용할 최신 feel 블렌드 값입니다.
                float feelingBlendValue = Mathf.Clamp01(_resultFeelBlend.Value);
                _animator.SetFloat(_feelBlendParam, feelingBlendValue);
                _animator.SetTrigger(_feelParam);
                break;
        }

        _lastAnimState = nextState;
    }

    /// <summary>현재 이동 상태를 SFX 컨트롤러에 전달해 발소리 조건을 갱신합니다.</summary>
    private void UpdateMovementSfxState(bool isGrounded, float horizontalSpeed, bool hasMoveInput)
    {
        if (_playerSfxController == null)
            return;

        _playerSfxController.SetMovementState(isGrounded, horizontalSpeed, hasMoveInput);
    }

    /// <summary>점프 상태 진입 시 점프 SFX를 1회 재생합니다.</summary>
    private void PlayJumpSfx()
    {
        if (_playerSfxController == null)
            return;

        _playerSfxController.PlayJump();
    }

    /// <summary>
    /// 서버에서 장애물 피격 사운드 재생을 요청하고 오너 클라이언트에 전달합니다.
    /// </summary>
    public void PlayObstacleHitSfx_Server()
    {
        if (!IsServer)
            return;

        PlayObstacleHitSfx_ClientRpc();
    }

    /// <summary>
    /// 오너 클라이언트에서 장애물 피격 SFX를 실제로 재생합니다.
    /// </summary>
    [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server, Delivery = RpcDelivery.Unreliable)]
    private void PlayObstacleHitSfx_ClientRpc()
    {
        if (_playerSfxController == null)
            return;

        _playerSfxController.PlayObstacleHit();
    }

    /// <summary>
    /// GameSessionController 생성 타이밍이 늦더라도 캐릭터 배정/상태 이벤트 구독을 보장합니다.
    /// </summary>
    private void EnsureCharacterAssignmentEventSubscription()
    {
        if (_characterAssignmentEventsSubscribed)
            return;

        GameSessionController session = GameSessionController.Instance;
        if (session == null)
        {
            _characterAssignmentEventsSubscribed = false;
            return;
        }

        session.RemoveCharacterAssignmentListener(OnCharacterAssignmentChanged);
        session.AddCharacterAssignmentListener(OnCharacterAssignmentChanged);
        session.RemoveStateListener(OnGameStateChanged);
        session.AddStateListener(OnGameStateChanged);
        _characterAssignmentEventsSubscribed = true;
    }

    /// <summary>
    /// 기존 호출부 호환을 위해 캐릭터 배정 이벤트 구독 보장 함수를 호출합니다.
    /// </summary>
    private void SubscribeCharacterAssignmentEvents()
    {
        EnsureCharacterAssignmentEventSubscription();
    }

    /// <summary>
    /// GameSession 이벤트 구독을 해제합니다.
    /// </summary>
    private void UnsubscribeCharacterAssignmentEvents()
    {
        if (!_characterAssignmentEventsSubscribed)
            return;

        GameSessionController session = GameSessionController.Instance;
        if (session == null)
        {
            _characterAssignmentEventsSubscribed = false;
            return;
        }

        session.RemoveCharacterAssignmentListener(OnCharacterAssignmentChanged);
        session.RemoveStateListener(OnGameStateChanged);
        _characterAssignmentEventsSubscribed = false;
    }

    /// <summary>
    /// 세션 상태가 Lobby로 돌아오면 결과 애니메이션 상태를 초기화합니다.
    /// </summary>
    private void OnGameStateChanged(E_GameSessionState prev, E_GameSessionState next)
    {
        if (!IsServer)
            return;

        if (next == E_GameSessionState.Lobby || next == E_GameSessionState.Countdown || next == E_GameSessionState.Running)
            ClearResultFeeling_Server();
    }

    /// <summary>
    /// 캐릭터 배정 변경 이벤트를 받아 자신의 비주얼을 갱신합니다.
    /// </summary>
    private void OnCharacterAssignmentChanged(CharacterAssignmentEntry entry, NetworkListEvent<CharacterAssignmentEntry>.EventType eventType)
    {
        if (entry.clientId != OwnerClientId)
            return;

        if (eventType == NetworkListEvent<CharacterAssignmentEntry>.EventType.Remove)
            return;

        TryApplyAssignedCharacterVisual();
    }

    /// <summary>
    /// 자신의 배정 캐릭터를 읽어 비주얼 프리팹을 attach/spawn 합니다.
    /// </summary>
    private void TryApplyAssignedCharacterVisual()
    {
        if (IsServer)
            TrySyncCharacterStateFromSession_Server();

        ApplyCharacterVisualFromNetworkState();
    }

    /// <summary>
    /// 서버가 GameSession의 배정 결과를 NetworkVariable 단일 상태값으로 반영합니다.
    /// </summary>
    private void TrySyncCharacterStateFromSession_Server()
    {
        if (!IsServer)
            return;

        GameSessionController session = GameSessionController.Instance;
        if (session == null)
            return;

        if (!session.TryGetAssignedCharacter(OwnerClientId, out string characterId, out bool isFallback))
            return;

        bool changed = _networkCharacterId.Value.ToString() != characterId || _networkCharacterIsFallback.Value != isFallback;
        _networkCharacterId.Value = new FixedString64Bytes(characterId ?? string.Empty);
        _networkCharacterIsFallback.Value = isFallback;

        if (changed)
            Debug.Log($"[CharacterSync] Sync owner={OwnerClientId}, characterId={characterId}, fallback={isFallback}");
    }

    /// <summary>
    /// 네트워크 상태값을 읽어 현재 플레이어 비주얼을 적용합니다.
    /// </summary>
    private void ApplyCharacterVisualFromNetworkState()
    {
        string characterId = _networkCharacterId.Value.ToString();
        bool isFallback = _networkCharacterIsFallback.Value;

        if (string.IsNullOrEmpty(characterId))
            return;

        if (_activeVisual != null && characterId == _lastAppliedCharacterId && isFallback == _lastAppliedFallback)
            return;

        _lastAppliedCharacterId = characterId;
        _lastAppliedFallback = isFallback;
        Debug.Log($"[CharacterSync] Apply owner={OwnerClientId}, local={NetworkManager.LocalClientId}, characterId={characterId}, fallback={isFallback}");
        SpawnCharacterVisual(characterId, isFallback);
    }

    /// <summary>
    /// 캐릭터 ID 네트워크 상태값이 변경되면 비주얼 적용을 재시도합니다.
    /// </summary>
    private void OnNetworkCharacterStateChanged(FixedString64Bytes previous, FixedString64Bytes current)
    {
        Debug.Log($"[CharacterSync] CharacterIdChanged owner={OwnerClientId}, local={NetworkManager.LocalClientId}, prev={previous}, next={current}");
        ApplyCharacterVisualFromNetworkState();
    }

    /// <summary>
    /// fallback 네트워크 상태값이 변경되면 비주얼 적용을 재시도합니다.
    /// </summary>
    private void OnNetworkCharacterFallbackChanged(bool previous, bool current)
    {
        Debug.Log($"[CharacterSync] FallbackChanged owner={OwnerClientId}, local={NetworkManager.LocalClientId}, prev={previous}, next={current}");
        ApplyCharacterVisualFromNetworkState();
    }

    /// <summary>
    /// 캐릭터 ID를 기준으로 비주얼을 생성하고 Animator 참조를 연결합니다.
    /// </summary>
    private void SpawnCharacterVisual(string characterId, bool forceFallback)
    {
        if (_activeVisual != null)
            Destroy(_activeVisual);

        GameObject prefab = null;

        if (!forceFallback && CharacterCatalogRuntime.TryGetDefinition(characterId, out CharacterCatalog.CharacterDefinition definition))
            prefab = CharacterVisualFactory.TryLoadCharacterPrefab(definition.loaderKey);

        if (prefab == null)
            _activeVisual = CharacterVisualFactory.CreateFallbackCapsule($"Fallback_{OwnerClientId}");
        else
            _activeVisual = Instantiate(prefab);

        _activeVisual.transform.SetParent(_visualRoot, false);
        _activeVisual.transform.localPosition = Vector3.zero;
        _activeVisual.transform.localRotation = Quaternion.identity;

        _animator = _activeVisual.GetComponentInChildren<Animator>();
        if (_animator == null && prefab != null)
        {
            Destroy(_activeVisual);
            _activeVisual = CharacterVisualFactory.CreateFallbackCapsule($"Fallback_NoAnimator_{OwnerClientId}");
            _activeVisual.transform.SetParent(_visualRoot, false);
            _activeVisual.transform.localPosition = Vector3.zero;
            _activeVisual.transform.localRotation = Quaternion.identity;
        }
    }

    /// <summary>
    /// 결과 연출 활성화 값이 변경될 때 feel 블렌드 값을 먼저 반영한 뒤 feel 트리거를 발행합니다.
    /// </summary>
    private void OnResultFeelingChanged(bool previous, bool current)
    {
        if (current)
        {
            // feel 시작 직전에 반영할 네트워크 동기화 블렌드 값입니다.
            float synchronizedFeelBlend = Mathf.Clamp01(_resultFeelBlend.Value);
            _animator?.SetFloat(_feelBlendParam, synchronizedFeelBlend);
            TriggerAnimatorState(MotionAnimState.Feeling);
            return;
        }

        HandleResultFeelingCleared();
    }

    /// <summary>
    /// 결과 연출 종료 시 feel 트리거와 상태 캐시를 정리해 다른 이동 애니메이션으로 전이가 가능하도록 만듭니다.
    /// </summary>
    private void HandleResultFeelingCleared()
    {
        if (_animator == null)
            return;

        _animator.ResetTrigger(_feelParam);
        _animator.SetFloat(_feelBlendParam, 0f);

        // feel 종료 직후 동일 상태 중복 차단이 남지 않도록 캐시를 초기 상태로 되돌립니다.
        _lastAnimState = MotionAnimState.Idle;
        _animator.SetTrigger(_idleParam);
    }

    /// <summary>
    /// 결과 연출 블렌드 값이 변경될 때 Animator float 파라미터를 갱신합니다.
    /// </summary>
    private void OnResultFeelingBlendChanged(float previous, float current)
    {
        if (_animator == null)
            return;

        _animator.SetFloat(_feelBlendParam, current);
    }

    private bool CanMove_Server()
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerMotorServer] Fallback 발생: CanMove_Server called on non-server.");
            return false;
        }

        GameSessionController session = GameSessionController.Instance;
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

    /// <summary>
    /// 플레이어 하단 Raycast로 접지 상태를 검사합니다.
    /// </summary>
    private bool TryGetGroundHit(out RaycastHit hit)
    {
        // 캡슐/콜라이더 구조에 따라 조정
        Vector3 origin = transform.position + Vector3.up * 0.1f;
        float downwardSpeed = Mathf.Max(0f, -_rb.linearVelocity.y);
        float dynamicDistance = _groundCheckDistance + (downwardSpeed * _groundCheckFallDistancePerSpeed);
        bool hitGround = Physics.Raycast(origin, Vector3.down, out hit, dynamicDistance, _groundMask, QueryTriggerInteraction.Ignore);
        if (!hitGround)
            return false;

        // 상승 플랫폼 탑승 상태는 예외적으로 접지를 유지합니다.
        if (TryResolveGroundedPlatform(hit, out _))
            return true;

        // 상승 중 오검출을 줄이기 위해 하강 또는 정지 상태에서만 접지로 판정합니다.
        return _rb.linearVelocity.y <= 0f;
    }

    /// <summary>
    /// 서버가 관리하는 접촉 목록과 Ground 히트를 함께 확인해 현재 탑승 플랫폼을 결정합니다.
    /// </summary>
    private bool TryResolveGroundedPlatform(RaycastHit hit, out MovingPlatformController platform)
    {
        platform = hit.collider != null ? hit.collider.GetComponentInParent<MovingPlatformController>() : null;
        if (platform == null)
            return false;

        return platform.IsPlayerInContact(this);
    }

    /// <summary>
    /// 상단 충돌 감지로 천장 압착 상황에서 상승 속도를 차단합니다.
    /// </summary>
    private bool HasCeilingAbove()
    {
        Vector3 origin = transform.position + Vector3.up * 0.1f;
        return Physics.Raycast(origin, Vector3.up, _headCheckDistance, _headCheckMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    /// 플랫폼 접촉 시작 시 서버가 현재 플랫폼 후보를 캐시합니다.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer)
            return;

        MovingPlatformController platform = collision.collider.GetComponentInParent<MovingPlatformController>();
        if (platform != null)
            _currentPlatform = platform;
    }

    /// <summary>
    /// 플랫폼 접촉 종료 시 서버가 플랫폼 캐시를 즉시 해제합니다.
    /// </summary>
    private void OnCollisionExit(Collision collision)
    {
        if (!IsServer)
            return;

        MovingPlatformController platform = collision.collider.GetComponentInParent<MovingPlatformController>();
        if (platform != null && platform == _currentPlatform)
            _currentPlatform = null;
    }
}
