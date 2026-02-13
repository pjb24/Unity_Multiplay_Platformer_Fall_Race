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
    [SerializeField] private float _jumpVelocity = 6.5f;
    [SerializeField] private LayerMask _groundMask;
    [SerializeField] private float _groundCheckDistance = 1.15f;

    [Header("Moving Platform")]
    [SerializeField] private float _jumpPlatformVelocityInherit = 0.85f;

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
    [SerializeField] private string _getupParam = "getup";
    [SerializeField] private string _jumpParam = "jump";
    [SerializeField] private string _feelBlendParam = "blend feel";
    /// <summary>이동 속도(0~1)를 전달할 Animator float 파라미터 이름입니다.</summary>
    [SerializeField] private string _speedParam = "Speed";

    [Header("Visual")]
    [SerializeField] private Transform _visualRoot;

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

    /// <summary>현재 표시 중인 캐릭터 비주얼 인스턴스입니다.</summary>
    private GameObject _activeVisual;
    /// <summary>현재 캐릭터의 Animator 참조입니다.</summary>
    private Animator _animator;

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

    /// <summary>Animator Speed 파라미터가 비어있는지 확인하는 플래그입니다.</summary>
    private bool _hasSpeedParam;

    private void Awake()
    {
        // 플레이어 이동 제어에 사용하는 Rigidbody를 캐싱합니다.
        _rb = GetComponent<Rigidbody>();
        // 비주얼 부모가 지정되지 않으면 자기 자신을 사용합니다.
        if (_visualRoot == null)
            _visualRoot = transform;

        // 발이 지면에서 떨어지는 시점 기준값 초기화입니다.
        _liftOffY = transform.position.y;
        _wasAirborne = false;
        _fallStateLockedUntilGrounded = false;
    }

    public override void OnNetworkSpawn()
    {
        // 결과 연출 동기화 값 변화를 구독합니다.
        _resultFeelActive.OnValueChanged += OnResultFeelingChanged;
        _resultFeelBlend.OnValueChanged += OnResultFeelingBlendChanged;

        // 캐릭터 배정 변경 이벤트를 구독합니다.
        SubscribeCharacterAssignmentEvents();
        // 이미 배정된 캐릭터가 있으면 즉시 비주얼을 구성합니다.
        TryApplyAssignedCharacterVisual();
    }

    public override void OnNetworkDespawn()
    {
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
    /// 서버에서 결과 애니메이션(feel + blend feel)을 설정합니다.
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
    }

    private void Update()
    {
        // 비주얼이 아직 준비되지 않았다면 계속 재시도합니다.
        if (_activeVisual == null)
            TryApplyAssignedCharacterVisual();

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
        Vector3 platformVelocity = Vector3.zero;
        if (grounded)
        {
            MovingPlatformController platform = hit.collider != null ? hit.collider.GetComponentInParent<MovingPlatformController>() : null;
            if (platform != null)
                platformVelocity = platform.CurrentVelocity;
        }

        // 이동
        float stateSpeedMultiplier = _inputStrength >= _runInputThreshold ? 1f : _walkSpeedMultiplier;
        float maxStateSpeed = _maxSpeed * stateSpeedMultiplier;
        float controlRatio = grounded ? 1f : Mathf.Clamp01(_airControl);

        Vector3 movePlaneNormal = GetMovePlaneNormal(grounded, hit);
        Vector3 targetHorizontalVelocity = Vector3.ProjectOnPlane(_inputDirection * (_inputStrength * maxStateSpeed), movePlaneNormal);
        targetHorizontalVelocity *= controlRatio;
        if (grounded)
            targetHorizontalVelocity += new Vector3(platformVelocity.x, 0f, platformVelocity.z);

        Vector3 currentVelocity = _rb.linearVelocity;
        Vector3 currentHorizontalVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
        float accelerationRate = GetAccelerationRate(grounded, currentHorizontalVelocity, targetHorizontalVelocity);
        Vector3 nextHorizontalVelocity = Vector3.MoveTowards(
            currentHorizontalVelocity,
            targetHorizontalVelocity,
            accelerationRate * Time.fixedDeltaTime);

        float maxHorizontalMagnitude = (grounded ? maxStateSpeed : maxStateSpeed * controlRatio) + new Vector3(platformVelocity.x, 0f, platformVelocity.z).magnitude;
        nextHorizontalVelocity = Vector3.ClampMagnitude(nextHorizontalVelocity, maxHorizontalMagnitude);
        if (_inputStrength <= 0f && nextHorizontalVelocity.magnitude <= _stopThreshold)
            nextHorizontalVelocity = Vector3.zero;

        // 현재 수평 속도 방향 기준으로 캐릭터 회전을 갱신합니다.
        RotateCharacter_Server(nextHorizontalVelocity);

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
            currentVelocity.y = _jumpVelocity;
            currentVelocity.x += platformVelocity.x * Mathf.Clamp01(_jumpPlatformVelocityInherit);
            currentVelocity.z += platformVelocity.z * Mathf.Clamp01(_jumpPlatformVelocityInherit);
            _rb.linearVelocity = currentVelocity;
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

        UpdateAnimatorSpeedValue();

        if (_resultFeelActive.Value)
        {
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

            TriggerAnimatorState(shouldFall ? MotionAnimState.Fall : MotionAnimState.Jump);
            return;
        }

        _landedThisFrame = _wasAirborne || _fallStateLockedUntilGrounded;

        if (_fallStateLockedUntilGrounded)
        {
            if (_lastAnimState == MotionAnimState.Fall)
                _animator.SetTrigger(_getupParam);

            _isFalling = false;
            _wasAirborne = false;
            _fallStateLockedUntilGrounded = false;
            return;
        }

        _isFalling = false;
        _wasAirborne = false;
        Vector3 horizontalVelocity = _rb != null ? new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z) : Vector3.zero;
        
        bool hasMoveInput = _inputStrength > 0f;
        float runThreshold = Mathf.Max(_stopThreshold, _runSpeedThreshold);
        bool isRunning = hasMoveInput || horizontalVelocity.magnitude >= runThreshold;

        // 착지 직후 이동 입력이 유지되고 있으면 즉시 run 전환을 허용합니다.
        if (_landedThisFrame && hasMoveInput)
            isRunning = true;

        TriggerAnimatorState(isRunning ? MotionAnimState.Running : MotionAnimState.Idle);
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
    /// 실제 수평 이동 속도를 Animator Speed 파라미터로 동기화합니다.
    /// </summary>
    private void UpdateAnimatorSpeedValue()
    {
        if (!_hasSpeedParam)
            return;

        Vector3 horizontalVelocity = _rb != null
            ? new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z)
            : Vector3.zero;
        float normalizedSpeed = Mathf.Clamp01(horizontalVelocity.magnitude / Mathf.Max(0.0001f, _maxSpeed));
        _animator.SetFloat(_speedParam, normalizedSpeed);
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
                break;
            case MotionAnimState.Fall:
                _animator.SetTrigger(_fallParam);
                break;
            case MotionAnimState.Feeling:
                _animator.SetTrigger(_feelParam);
                break;
        }

        _lastAnimState = nextState;
    }

    /// <summary>
    /// GameSession의 캐릭터 배정 이벤트를 구독합니다.
    /// </summary>
    private void SubscribeCharacterAssignmentEvents()
    {
        GameSessionController session = GameSessionController.Instance;
        if (session == null)
            return;

        session.RemoveCharacterAssignmentListener(OnCharacterAssignmentChanged);
        session.AddCharacterAssignmentListener(OnCharacterAssignmentChanged);
        session.RemoveStateListener(OnGameStateChanged);
        session.AddStateListener(OnGameStateChanged);
    }

    /// <summary>
    /// GameSession 이벤트 구독을 해제합니다.
    /// </summary>
    private void UnsubscribeCharacterAssignmentEvents()
    {
        GameSessionController session = GameSessionController.Instance;
        if (session == null)
            return;

        session.RemoveCharacterAssignmentListener(OnCharacterAssignmentChanged);
        session.RemoveStateListener(OnGameStateChanged);
    }

    /// <summary>
    /// 세션 상태가 Lobby로 돌아오면 결과 애니메이션 상태를 초기화합니다.
    /// </summary>
    private void OnGameStateChanged(E_GameSessionState prev, E_GameSessionState next)
    {
        if (!IsServer)
            return;

        if (next == E_GameSessionState.Lobby)
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
        GameSessionController session = GameSessionController.Instance;
        if (session == null)
            return;

        if (!session.TryGetAssignedCharacter(OwnerClientId, out string characterId, out bool isFallback))
            return;

        SpawnCharacterVisual(characterId, isFallback);
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
        _hasSpeedParam = _animator != null && !string.IsNullOrWhiteSpace(_speedParam);
        if (_animator == null && prefab != null)
        {
            Destroy(_activeVisual);
            _activeVisual = CharacterVisualFactory.CreateFallbackCapsule($"Fallback_NoAnimator_{OwnerClientId}");
            _activeVisual.transform.SetParent(_visualRoot, false);
            _activeVisual.transform.localPosition = Vector3.zero;
            _activeVisual.transform.localRotation = Quaternion.identity;
        }

        if (_animator == null)
            _hasSpeedParam = false;
    }

    /// <summary>
    /// 결과 연출 활성화 값이 변경될 때 feel 트리거를 발행합니다.
    /// </summary>
    private void OnResultFeelingChanged(bool previous, bool current)
    {
        if (!current)
            return;

        TriggerAnimatorState(MotionAnimState.Feeling);
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
        return Physics.Raycast(origin, Vector3.down, out hit, _groundCheckDistance, _groundMask, QueryTriggerInteraction.Ignore);
    }
}
