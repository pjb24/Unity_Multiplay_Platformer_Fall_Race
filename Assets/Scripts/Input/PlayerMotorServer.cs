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

    [Header("Animation Threshold")]
    [SerializeField] private float _runSpeedThreshold = 0.15f;
    [SerializeField] private float _fallDistanceFromLiftOffThreshold = 0.15f;

    [Header("Animation Param Names")]
    [SerializeField] private string _idleParam = "idle";
    [SerializeField] private string _runParam = "run";
    [SerializeField] private string _fallParam = "fall";
    [SerializeField] private string _feelParam = "feel";
    [SerializeField] private string _getupParam = "getup";
    [SerializeField] private string _jumpParam = "jump";
    [SerializeField] private string _feelBlendParam = "blend feel";

    [Header("Visual")]
    [SerializeField] private Transform _visualRoot;

    [Header("Debug")]
    [SerializeField] private bool _logStopImmediately = false;

    /// <summary>물리 이동 처리에 사용하는 Rigidbody 참조입니다.</summary>
    private Rigidbody _rb;

    /// <summary>서버가 수신한 최신 이동 입력 벡터입니다.</summary>
    private Vector2 _move;
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

    /// <summary>서버에서 결과 연출 활성화를 동기화하는 플래그입니다.</summary>
    private readonly NetworkVariable<bool> _resultFeelActive = new NetworkVariable<bool>(false);
    /// <summary>서버에서 결과 연출 win/lose 블렌드 값을 동기화하는 변수입니다.</summary>
    private readonly NetworkVariable<float> _resultFeelBlend = new NetworkVariable<float>(0f);

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

        bool grounded = TryGetGroundHit(out RaycastHit hit);
        Vector3 platformVelocity = Vector3.zero;
        if (grounded)
        {
            MovingPlatformController platform = hit.collider != null ? hit.collider.GetComponentInParent<MovingPlatformController>() : null;
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

    /// <summary>
    /// 이동/물리 상태 우선순위(feel > fall > jump > run > idle)로 애니메이션 트리거를 발행합니다.
    /// </summary>
    private void UpdateMotionAnimation()
    {
        if (_animator == null)
            return;

        if (_resultFeelActive.Value)
        {
            TriggerAnimatorState(MotionAnimState.Feeling);
            return;
        }

        bool grounded = TryGetGroundHit(out _);
        float currentY = transform.position.y;

        if (!grounded)
        {
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
        bool isRunning = horizontalVelocity.magnitude >= _runSpeedThreshold;
        TriggerAnimatorState(isRunning ? MotionAnimState.Running : MotionAnimState.Idle);
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
