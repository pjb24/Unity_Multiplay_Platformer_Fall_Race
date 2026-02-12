using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 밟으면 사라졌다가 다시 나타나는 플랫폼(서버 권위).
/// 상태 흐름: Idle -> Warning -> Hidden -> Idle
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public sealed class TimedDisappearPlatformController : NetworkBehaviour
{
    /// <summary>
    /// 플랫폼의 네트워크 동기화 상태.
    /// </summary>
    public enum PlatformState : byte
    {
        Idle = 0,
        Warning = 1,
        Hidden = 2,
    }

    [Header("Timing")]
    // 밟힘 이후 Warning 상태로 전환되기까지의 지연 시간(초).
    [SerializeField, Min(0f)] private float _warningDelaySec = 1f;
    // Warning 시작 후 플랫폼이 실제로 사라질 때까지의 지연 시간(초).
    [SerializeField, Min(0f)] private float _disappearDelayAfterWarningSec = 1f;
    // Hidden 상태에서 Idle로 복귀(재등장)하기까지의 지연 시간(초).
    [SerializeField, Min(0f)] private float _respawnDelaySec = 1.6f;
    // 리스폰 직후 즉시 재트리거를 막기 위한 추가 쿨다운 시간(초).
    [SerializeField, Min(0f)] private float _retriggerCooldownSec = 0f;

    [Header("Trigger")]
    // 트리거를 허용할 플레이어 태그.
    [SerializeField] private string _requirePlayerTag = "Player";
    // 한 사이클 동안 중복 트리거를 막을지 여부.
    [SerializeField] private bool _oneShotPerCycle = true;

    [Header("Warning Feedback")]
    // 경고 피드백을 적용할 렌더러 목록.
    [SerializeField] private Renderer[] _targetRenderers;
    // 경고 시 점멸에 사용할 기준 색상.
    [SerializeField] private Color _warningColor = new(1f, 0.45f, 0.2f, 1f);
    // 경고 점멸 속도(Hz).
    [SerializeField, Min(0.1f)] private float _warningBlinkHz = 8f;

    [Header("Visibility")]
    // 숨김/복구 시 on/off할 콜라이더 목록.
    [SerializeField] private Collider[] _targetColliders;

    [Header("Debug")]
    // 추가 동기화/트리거 로그를 출력할지 여부.
    [SerializeField] private bool _verboseLog;

    // 서버 확정 플랫폼 상태(클라 읽기 전용).
    private readonly NetworkVariable<PlatformState> _state = new(
        PlatformState.Idle,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // 다음 상태 전이가 발생할 서버 절대 시각.
    private readonly NetworkVariable<double> _nextTransitionServerTime = new(
        0d,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // 현재 사이클을 트리거한 플레이어 ID(디버그 용도).
    private readonly NetworkVariable<ulong> _lastTriggeredPlayerId = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // 머티리얼 인스턴스 생성 없이 색상 오버라이드를 적용하기 위한 재사용 블록.
    private MaterialPropertyBlock _mpb;
    // 각 렌더러의 원본 색상 캐시(Warning 종료 후 복원용).
    private Color[] _baseColors;

    // Idle 상태에서 Warning 예약이 들어간 상태인지 표시.
    private bool _cycleScheduled;
    // Warning 시작 예정 서버 시각.
    private double _warningAtServerTime;
    // 다음 트리거를 허용할 최소 서버 시각(재트리거 쿨다운).
    private double _nextRetriggerAllowedServerTime;

    /// <summary>
    /// 컴포넌트 추가/리셋 시 렌더러와 콜라이더 참조를 자동 수집한다.
    /// </summary>
    private void Reset()
    {
        _targetRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        _targetColliders = GetComponentsInChildren<Collider>(includeInactive: true);
    }

    /// <summary>
    /// 초기 참조/색상 캐시를 준비하고 시작 비주얼을 적용한다.
    /// </summary>
    private void Awake()
    {
        EnsureReferences();
        CacheBaseColors();
        ApplyStateVisual(_state.Value, forceVisible: true);
    }

    /// <summary>
    /// 네트워크 스폰 시 서버/클라이언트 공통 초기화 및 이벤트 바인딩을 수행한다.
    /// </summary>
    public override void OnNetworkSpawn()
    {
        _state.OnValueChanged += OnStateChanged;
        _nextTransitionServerTime.OnValueChanged += OnNextTransitionChanged;

        ApplyStateVisual(_state.Value, forceVisible: _state.Value != PlatformState.Hidden);

        if (IsServer)
        {
            _state.Value = PlatformState.Idle;
            _nextTransitionServerTime.Value = 0d;
            _lastTriggeredPlayerId.Value = 0;
            _cycleScheduled = false;
            _nextRetriggerAllowedServerTime = 0d;
        }
    }

    /// <summary>
    /// 네트워크 디스폰 시 바인딩된 이벤트를 해제한다.
    /// </summary>
    public override void OnNetworkDespawn()
    {
        _state.OnValueChanged -= OnStateChanged;
        _nextTransitionServerTime.OnValueChanged -= OnNextTransitionChanged;
    }

    /// <summary>
    /// 매 프레임 경고 비주얼을 갱신하고, 서버에서는 상태 전이를 처리한다.
    /// </summary>
    private void Update()
    {
        TickVisualFeedback();

        if (!IsServer)
            return;

        TickServerTransitions();
    }

    /// <summary>
    /// 충돌 시작 시 서버 트리거를 시도한다.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        TryTriggerByCollider_Server(collision.collider);
    }

    /// <summary>
    /// 충돌 유지 중 서버 트리거를 시도한다.
    /// </summary>
    private void OnCollisionStay(Collision collision)
    {
        TryTriggerByCollider_Server(collision.collider);
    }

    /// <summary>
    /// 트리거 진입 시 서버 트리거를 시도한다.
    /// </summary>
    private void OnTriggerEnter(Collider other)
    {
        TryTriggerByCollider_Server(other);
    }

    /// <summary>
    /// 트리거 유지 중 서버 트리거를 시도한다.
    /// </summary>
    private void OnTriggerStay(Collider other)
    {
        TryTriggerByCollider_Server(other);
    }

    /// <summary>
    /// 서버에서만 유효한 플레이어 판별 후 사이클 시작을 시도한다.
    /// </summary>
    private void TryTriggerByCollider_Server(Collider other)
    {
        if (!IsServer || other == null)
            return;

        if (!string.IsNullOrEmpty(_requirePlayerTag) && !other.CompareTag(_requirePlayerTag))
            return;

        var playerNetObj = other.GetComponentInParent<NetworkObject>();
        if (playerNetObj == null)
            return;

        if (playerNetObj.TryGetComponent<PlayerInputSender>(out _) == false &&
            playerNetObj.TryGetComponent<PlayerMotorServer>(out _) == false)
        {
            return;
        }

        TryStartCycle_Server(playerNetObj.OwnerClientId);
    }

    /// <summary>
    /// Idle 상태에서만 Warning 예약을 등록한다.
    /// </summary>
    private void TryStartCycle_Server(ulong playerId)
    {
        double now = NetworkManager.ServerTime.Time;

        if (_state.Value != PlatformState.Idle)
            return;

        if (_oneShotPerCycle && _cycleScheduled)
            return;

        if (now < _nextRetriggerAllowedServerTime)
            return;

        _cycleScheduled = true;
        _warningAtServerTime = now + _warningDelaySec;
        _nextTransitionServerTime.Value = _warningAtServerTime;
        _lastTriggeredPlayerId.Value = playerId;

        if (_verboseLog)
            Debug.Log($"[PlatformCycle] trigger platform={NetworkObjectId} by={playerId} warningAt={_warningAtServerTime:F3}");
    }

    /// <summary>
    /// 서버 시간 기준으로 상태 만료를 검사하고 다음 상태로 전환한다.
    /// </summary>
    private void TickServerTransitions()
    {
        double now = NetworkManager.ServerTime.Time;

        if (_state.Value == PlatformState.Idle)
        {
            if (_cycleScheduled && now >= _warningAtServerTime)
                EnterWarning_Server(now);
            return;
        }

        if (_nextTransitionServerTime.Value <= 0d || now < _nextTransitionServerTime.Value)
            return;

        if (_state.Value == PlatformState.Warning)
        {
            EnterHidden_Server(now);
            return;
        }

        if (_state.Value == PlatformState.Hidden)
            EnterRespawn_Server(now);
    }

    /// <summary>
    /// Warning 상태로 진입하고 Hidden 전이 시각을 예약한다.
    /// </summary>
    private void EnterWarning_Server(double now)
    {
        _state.Value = PlatformState.Warning;
        _nextTransitionServerTime.Value = now + _disappearDelayAfterWarningSec;

        Debug.Log($"[PlatformCycle] warning platform={NetworkObjectId} at={now:F3}");
    }

    /// <summary>
    /// Hidden 상태로 진입하고 Respawn(Idle) 시각을 예약한다.
    /// </summary>
    private void EnterHidden_Server(double now)
    {
        _state.Value = PlatformState.Hidden;
        _nextTransitionServerTime.Value = now + _respawnDelaySec;

        Debug.Log($"[PlatformCycle] hidden platform={NetworkObjectId} at={now:F3}");
    }

    /// <summary>
    /// Idle로 복귀시키고 다음 사이클을 위한 런타임 값을 초기화한다.
    /// </summary>
    private void EnterRespawn_Server(double now)
    {
        _state.Value = PlatformState.Idle;
        _nextTransitionServerTime.Value = 0d;
        _cycleScheduled = false;
        _warningAtServerTime = 0d;
        _nextRetriggerAllowedServerTime = now + _retriggerCooldownSec;

        Debug.Log($"[PlatformCycle] respawn platform={NetworkObjectId} at={now:F3}");
    }

    /// <summary>
    /// 상태 동기화 변경 시 렌더러/콜라이더 비주얼을 즉시 반영한다.
    /// </summary>
    private void OnStateChanged(PlatformState previous, PlatformState current)
    {
        ApplyStateVisual(current, forceVisible: current != PlatformState.Hidden);
    }

    /// <summary>
    /// 다음 전이 시각 동기화 변경을 디버그 로그로 확인한다.
    /// </summary>
    private void OnNextTransitionChanged(double previous, double current)
    {
        if (_verboseLog && IsClient)
            Debug.Log($"[PlatformCycle] sync platform={NetworkObjectId} state={_state.Value} next={current:F3}");
    }

    /// <summary>
    /// 현재 상태에 맞춰 가시성/충돌 여부를 일괄 적용한다.
    /// </summary>
    private void ApplyStateVisual(PlatformState state, bool forceVisible)
    {
        bool visible = forceVisible;
        if (state == PlatformState.Hidden)
            visible = false;

        for (int i = 0; i < _targetRenderers.Length; i++)
        {
            if (_targetRenderers[i] == null)
                continue;

            _targetRenderers[i].enabled = visible;
        }

        for (int i = 0; i < _targetColliders.Length; i++)
        {
            if (_targetColliders[i] == null)
                continue;

            _targetColliders[i].enabled = visible;
        }

        if (state != PlatformState.Warning)
            RestoreBaseColor();
    }

    /// <summary>
    /// Warning 상태에서 렌더러 색상을 점멸시켜 경고 피드백을 표현한다.
    /// </summary>
    private void TickVisualFeedback()
    {
        if (_state.Value != PlatformState.Warning)
            return;

        if (_targetRenderers == null || _targetRenderers.Length == 0)
            return;

        float t = Mathf.PingPong(Time.time * Mathf.Max(0.1f, _warningBlinkHz), 1f);
        Color blended = Color.Lerp(_warningColor, Color.white, t * 0.5f);

        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();

        for (int i = 0; i < _targetRenderers.Length; i++)
        {
            var renderer = _targetRenderers[i];
            if (renderer == null || !renderer.enabled)
                continue;

            renderer.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", blended);
            _mpb.SetColor("_Color", blended);
            renderer.SetPropertyBlock(_mpb);
        }
    }

    /// <summary>
    /// Warning 피드백으로 변경된 색상을 원본 색상으로 복원한다.
    /// </summary>
    private void RestoreBaseColor()
    {
        if (_targetRenderers == null)
            return;

        if (_mpb == null)
            _mpb = new MaterialPropertyBlock();

        for (int i = 0; i < _targetRenderers.Length; i++)
        {
            var renderer = _targetRenderers[i];
            if (renderer == null)
                continue;

            renderer.GetPropertyBlock(_mpb);
            Color baseColor = (i < _baseColors.Length) ? _baseColors[i] : Color.white;
            _mpb.SetColor("_BaseColor", baseColor);
            _mpb.SetColor("_Color", baseColor);
            renderer.SetPropertyBlock(_mpb);
        }
    }

    /// <summary>
    /// 인스펙터 미설정 시 하위 오브젝트에서 렌더러/콜라이더를 자동 탐색한다.
    /// </summary>
    private void EnsureReferences()
    {
        if (_targetRenderers == null || _targetRenderers.Length == 0)
            _targetRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);

        if (_targetColliders == null || _targetColliders.Length == 0)
            _targetColliders = GetComponentsInChildren<Collider>(includeInactive: true);
    }

    /// <summary>
    /// 렌더러의 기본 색상을 캐시해 상태 복귀 시 재사용한다.
    /// </summary>
    private void CacheBaseColors()
    {
        if (_targetRenderers == null)
        {
            _baseColors = System.Array.Empty<Color>();
            return;
        }

        _baseColors = new Color[_targetRenderers.Length];
        for (int i = 0; i < _targetRenderers.Length; i++)
        {
            var renderer = _targetRenderers[i];
            if (renderer == null || renderer.sharedMaterial == null)
            {
                _baseColors[i] = Color.white;
                continue;
            }

            if (renderer.sharedMaterial.HasProperty("_BaseColor"))
                _baseColors[i] = renderer.sharedMaterial.GetColor("_BaseColor");
            else if (renderer.sharedMaterial.HasProperty("_Color"))
                _baseColors[i] = renderer.sharedMaterial.GetColor("_Color");
            else
                _baseColors[i] = Color.white;
        }
    }
}
