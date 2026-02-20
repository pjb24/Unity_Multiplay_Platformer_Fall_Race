using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 이동/점프 효과음을 재생하는 전용 컨트롤러입니다.
/// </summary>
[DisallowMultipleComponent]
public sealed class PlayerSfxController : MonoBehaviour
{
    [Header("Audio Source")]
    /// <summary>발소리 재생에 사용하는 AudioSource 참조입니다.</summary>
    [SerializeField] private AudioSource _footstepSource;
    /// <summary>점프 사운드 재생에 사용하는 AudioSource 참조입니다.</summary>
    [SerializeField] private AudioSource _jumpSource;
    /// <summary>장애물 충돌 사운드 재생에 사용하는 AudioSource 참조입니다.</summary>
    [SerializeField] private AudioSource _obstacleHitSource;

    [Header("Clips")]
    /// <summary>이동 시 순환 재생할 발소리 클립 목록입니다.</summary>
    [SerializeField] private AudioClip[] _footstepClips;
    /// <summary>점프 성공 시 1회 재생할 점프 클립입니다.</summary>
    [SerializeField] private AudioClip _jumpClip;
    /// <summary>Goal 진입 시 1회 재생할 골 클립입니다. 미할당 시 점프 클립을 대체 사용합니다.</summary>
    [SerializeField] private AudioClip _goalClip;
    /// <summary>Pusher/Rotation Bar 충돌 시 랜덤 재생할 피격 클립 3종 목록입니다.</summary>
    [SerializeField] private AudioClip[] _obstacleHitClips = new AudioClip[3];

    [Header("Footstep")]
    /// <summary>발소리 기본 재생 간격(초)입니다.</summary>
    [SerializeField, Min(0.01f)] private float _footstepInterval = 0.32f;
    /// <summary>발소리 재생을 시작할 최소 수평 속도입니다.</summary>
    [SerializeField, Min(0f)] private float _minMoveSpeedForFootstep = 0.15f;
    /// <summary>발소리를 접지 상태에서만 재생할지 여부입니다.</summary>
    [SerializeField] private bool _groundedRequiredForFootstep = true;

    [Header("Volume & Pitch")]
    /// <summary>발소리 볼륨 크기입니다.</summary>
    [SerializeField, Range(0f, 1f)] private float _footstepVolume = 0.75f;
    /// <summary>점프 사운드 볼륨 크기입니다.</summary>
    [SerializeField, Range(0f, 1f)] private float _jumpVolume = 0.9f;
    /// <summary>Goal 사운드 볼륨 크기입니다.</summary>
    [SerializeField, Range(0f, 1f)] private float _goalVolume = 1.0f;
    /// <summary>장애물 충돌 사운드 볼륨 크기입니다.</summary>
    [SerializeField, Range(0f, 1f)] private float _obstacleHitVolume = 1f;
    /// <summary>반복감 완화를 위한 피치 랜덤 최소값입니다.</summary>
    [SerializeField, Range(0.5f, 2f)] private float _pitchMin = 0.96f;
    /// <summary>반복감 완화를 위한 피치 랜덤 최대값입니다.</summary>
    [SerializeField, Range(0.5f, 2f)] private float _pitchMax = 1.04f;

    [Header("Obstacle Hit Priority")]
    /// <summary>장애물 피격 사운드가 우선되도록 점프/발소리를 억제할 최소 시간(초)입니다.</summary>
    [SerializeField, Min(0f)] private float _obstacleSfxPriorityMinDuration = 0.2f;

    [Header("Policy")]
    /// <summary>로컬 오너 플레이어에게만 SFX를 재생할지 여부입니다.</summary>
    [SerializeField] private bool _playOnlyForLocalOwner = true;
    /// <summary>디버그 로그를 출력할지 여부입니다.</summary>
    [SerializeField] private bool _debugSfxLog = false;

    /// <summary>오너 판별에 사용하는 NetworkObject 참조입니다.</summary>
    private NetworkObject _networkObject;
    /// <summary>최신 접지 상태 캐시입니다.</summary>
    private bool _isGrounded;
    /// <summary>최신 이동 입력 여부 캐시입니다.</summary>
    private bool _hasMoveInput;
    /// <summary>최신 수평 속도 캐시입니다.</summary>
    private float _horizontalSpeed;
    /// <summary>다음 발소리 재생까지 남은 시간 캐시입니다.</summary>
    private float _footstepCooldown;
    /// <summary>점프 재생 중복 방지를 위해 남은 점프 재생 잠금 시간(초)입니다.</summary>
    private float _jumpPlaybackLockTimer;
    /// <summary>장애물 충돌 사운드 연속 재생을 제한하는 남은 잠금 시간(초)입니다.</summary>
    private float _obstacleHitPlaybackLockTimer;
    /// <summary>장애물 피격 사운드 우선 믹싱을 유지하는 남은 시간(초)입니다.</summary>
    private float _obstacleSfxPriorityTimer;

    /// <summary>
    /// 초기 참조를 캐싱하고 AudioSource 기본값을 보정합니다.
    /// </summary>
    private void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();

        if (_footstepSource == null)
            _footstepSource = GetComponent<AudioSource>();

        if (_jumpSource == null)
            _jumpSource = _footstepSource;

        if (_obstacleHitSource == null)
            _obstacleHitSource = _jumpSource;

        // 점프 사운드가 발소리 재생 상태에 영향을 받지 않도록 점프 전용 소스를 보장합니다.
        EnsureDedicatedJumpSource();
        // 장애물 피격 사운드가 다른 SFX 재생 상태에 영향을 받지 않도록 전용 소스를 보장합니다.
        EnsureDedicatedObstacleHitSource();

        if (_footstepSource != null)
            _footstepSource.playOnAwake = false;

        if (_jumpSource != null)
            _jumpSource.playOnAwake = false;

        if (_obstacleHitSource != null)
            _obstacleHitSource.playOnAwake = false;
    }

    /// <summary>
    /// 매 프레임 발소리 타이머를 갱신하고 조건이 충족되면 발소리를 재생합니다.
    /// </summary>
    private void Update()
    {
        // 런타임 재할당 상황에서도 점프/장애물 전용 소스를 유지합니다.
        EnsureDedicatedJumpSource();
        EnsureDedicatedObstacleHitSource();

        if (!CanPlaySfx())
            return;

        if (_jumpPlaybackLockTimer > 0f)
            _jumpPlaybackLockTimer = Mathf.Max(0f, _jumpPlaybackLockTimer - Time.deltaTime);

        if (_obstacleHitPlaybackLockTimer > 0f)
            _obstacleHitPlaybackLockTimer = Mathf.Max(0f, _obstacleHitPlaybackLockTimer - Time.deltaTime);

        if (_obstacleSfxPriorityTimer > 0f)
            _obstacleSfxPriorityTimer = Mathf.Max(0f, _obstacleSfxPriorityTimer - Time.deltaTime);

        if (IsObstacleSfxPriorityActive())
        {
            _footstepCooldown = 0f;
            StopFootstepIfPlaying();
            return;
        }

        if (!ShouldPlayFootstep())
        {
            _footstepCooldown = 0f;
            StopFootstepIfPlaying();
            return;
        }

        if (_footstepSource != null && _footstepSource.isPlaying)
            return;

        _footstepCooldown -= Time.deltaTime;
        if (_footstepCooldown > 0f)
            return;

        PlayFootstepInternal();
        _footstepCooldown = _footstepInterval;
    }

    /// <summary>
    /// 점프 사운드가 발소리 상태에 의해 차단되지 않도록 점프 전용 AudioSource를 보장합니다.
    /// </summary>
    private void EnsureDedicatedJumpSource()
    {
        if (_jumpSource == null || _footstepSource == null)
            return;

        if (_jumpSource != _footstepSource)
            return;

        AudioSource dedicatedJumpSource = gameObject.AddComponent<AudioSource>();
        dedicatedJumpSource.playOnAwake = false;
        dedicatedJumpSource.outputAudioMixerGroup = _footstepSource.outputAudioMixerGroup;
        dedicatedJumpSource.spatialBlend = _footstepSource.spatialBlend;
        dedicatedJumpSource.rolloffMode = _footstepSource.rolloffMode;
        dedicatedJumpSource.minDistance = _footstepSource.minDistance;
        dedicatedJumpSource.maxDistance = _footstepSource.maxDistance;
        dedicatedJumpSource.dopplerLevel = _footstepSource.dopplerLevel;

        _jumpSource = dedicatedJumpSource;

        if (_debugSfxLog)
            Debug.Log("[PlayerSfxController] Jump 전용 AudioSource를 자동 생성했습니다.");
    }

    /// <summary>
    /// 장애물 피격 사운드가 점프/발소리와 채널을 공유하지 않도록 전용 AudioSource를 보장합니다.
    /// </summary>
    private void EnsureDedicatedObstacleHitSource()
    {
        if (_obstacleHitSource == null)
            return;

        if (_obstacleHitSource != _footstepSource && _obstacleHitSource != _jumpSource)
            return;

        AudioSource referenceSource = _jumpSource != null ? _jumpSource : _footstepSource;
        if (referenceSource == null)
            return;

        AudioSource dedicatedObstacleSource = gameObject.AddComponent<AudioSource>();
        dedicatedObstacleSource.playOnAwake = false;
        dedicatedObstacleSource.outputAudioMixerGroup = referenceSource.outputAudioMixerGroup;
        dedicatedObstacleSource.spatialBlend = referenceSource.spatialBlend;
        dedicatedObstacleSource.rolloffMode = referenceSource.rolloffMode;
        dedicatedObstacleSource.minDistance = referenceSource.minDistance;
        dedicatedObstacleSource.maxDistance = referenceSource.maxDistance;
        dedicatedObstacleSource.dopplerLevel = referenceSource.dopplerLevel;

        _obstacleHitSource = dedicatedObstacleSource;

        if (_debugSfxLog)
            Debug.Log("[PlayerSfxController] ObstacleHit 전용 AudioSource를 자동 생성했습니다.");
    }

    /// <summary>
    /// 장애물 피격 사운드가 우선 믹싱되어야 하는 상태인지 판정합니다.
    /// </summary>
    private bool IsObstacleSfxPriorityActive()
    {
        return _obstacleSfxPriorityTimer > 0f;
    }

    /// <summary>
    /// 외부 이동 상태를 수신해 발소리 재생 판단 기준을 갱신합니다.
    /// </summary>
    public void SetMovementState(bool isGrounded, float horizontalSpeed, bool hasMoveInput)
    {
        _isGrounded = isGrounded;
        _horizontalSpeed = Mathf.Max(0f, horizontalSpeed);
        _hasMoveInput = hasMoveInput;
    }

    /// <summary>
    /// 점프 성공 시점에 호출되어 점프 효과음을 1회 재생합니다.
    /// </summary>
    public void PlayJump()
    {
        if (!CanPlaySfx())
            return;

        if (_jumpSource == null || _jumpClip == null)
        {
            if (_debugSfxLog)
                Debug.LogWarning("[PlayerSfxController] Jump 재생 실패: AudioSource 또는 Clip 누락");
            return;
        }

        if (_jumpPlaybackLockTimer > 0f)
            return;

        if (IsObstacleSfxPriorityActive())
            return;

        _jumpSource.pitch = Random.Range(_pitchMin, _pitchMax);
        _jumpSource.PlayOneShot(_jumpClip, _jumpVolume);
        _jumpPlaybackLockTimer = _jumpClip.length;
    }

    /// <summary>
    /// Goal 진입 시점에 호출되어 Goal 효과음을 1회 재생합니다.
    /// </summary>
    public void PlayGoal()
    {
        if (!CanPlaySfx())
            return;

        if (_jumpSource == null)
        {
            if (_debugSfxLog)
                Debug.LogWarning("[PlayerSfxController] Goal 재생 실패: AudioSource 누락");
            return;
        }

        // Goal 클립 미할당 시 기존 점프 클립을 대체 사용합니다.
        AudioClip goalClip = _goalClip != null ? _goalClip : _jumpClip;
        if (goalClip == null)
        {
            if (_debugSfxLog)
                Debug.LogWarning("[PlayerSfxController] Goal 재생 실패: Goal/Jump Clip 누락");
            return;
        }

        _jumpSource.pitch = Random.Range(_pitchMin, _pitchMax);
        _jumpSource.PlayOneShot(goalClip, _goalVolume);
    }

    /// <summary>
    /// 장애물(Pusher/Rotation Bar) 피격 시 충돌 SFX를 1회 재생합니다.
    /// </summary>
    public void PlayObstacleHit()
    {
        if (!CanPlaySfx())
            return;

        if (_obstacleHitSource == null || _obstacleHitClips == null || _obstacleHitClips.Length == 0)
        {
            if (_debugSfxLog)
                Debug.LogWarning("[PlayerSfxController] ObstacleHit 재생 실패: AudioSource 또는 Clip 배열 누락");
            return;
        }

        if (_obstacleHitPlaybackLockTimer > 0f)
            return;

        AudioClip clip = GetRandomObstacleHitClip();
        if (clip == null)
        {
            if (_debugSfxLog)
                Debug.LogWarning("[PlayerSfxController] ObstacleHit 재생 실패: 유효한 Clip이 없습니다.");
            return;
        }

        // 장애물 피격 사운드가 명확히 들리도록 발소리를 즉시 정지합니다.
        StopFootstepIfPlaying();

        _obstacleHitSource.pitch = Random.Range(_pitchMin, _pitchMax);
        _obstacleHitSource.PlayOneShot(clip, _obstacleHitVolume);

        // 장애물 피격 사운드 중복 재생을 제한하는 잠금 시간입니다.
        _obstacleHitPlaybackLockTimer = Mathf.Max(0.05f, clip.length * 0.5f);
        // 장애물 피격 사운드가 발소리/점프에 묻히지 않도록 우선 믹싱 유지 시간을 설정합니다.
        _obstacleSfxPriorityTimer = Mathf.Max(_obstacleSfxPriorityMinDuration, clip.length * 0.65f);
    }

    /// <summary>
    /// 장애물 피격 클립 3종 중 null이 아닌 항목에서 1개를 랜덤 선택합니다.
    /// </summary>
    private AudioClip GetRandomObstacleHitClip()
    {
        // null이 아닌 유효 장애물 피격 클립 개수를 계산합니다.
        int validCount = 0;

        for (int i = 0; i < _obstacleHitClips.Length; i++)
        {
            if (_obstacleHitClips[i] != null)
                validCount++;
        }

        if (validCount <= 0)
            return null;

        // 유효한 후보 인덱스 범위에서 하나를 무작위로 선택합니다.
        int selectedIndex = Random.Range(0, validCount);
        // 순회 중 현재 몇 번째 유효 클립인지 추적하는 인덱스입니다.
        int currentValidIndex = 0;

        for (int i = 0; i < _obstacleHitClips.Length; i++)
        {
            AudioClip clip = _obstacleHitClips[i];
            if (clip == null)
                continue;

            if (currentValidIndex == selectedIndex)
                return clip;

            currentValidIndex++;
        }

        return null;
    }

    /// <summary>
    /// 현재 상태가 발소리 재생 조건을 만족하는지 판정합니다.
    /// </summary>
    private bool ShouldPlayFootstep()
    {
        if (_groundedRequiredForFootstep && !_isGrounded)
            return false;

        if (!_hasMoveInput)
            return false;

        return _horizontalSpeed >= _minMoveSpeedForFootstep;
    }

    /// <summary>
    /// 내부 발소리 클립을 랜덤 선택하여 1회 재생합니다.
    /// </summary>
    private void PlayFootstepInternal()
    {
        if (_footstepSource == null || _footstepClips == null || _footstepClips.Length == 0)
        {
            if (_debugSfxLog)
                Debug.LogWarning("[PlayerSfxController] Footstep 재생 실패: AudioSource 또는 Clip 배열 누락");
            return;
        }

        AudioClip clip = _footstepClips[Random.Range(0, _footstepClips.Length)];
        if (clip == null)
            return;

        _footstepSource.pitch = Random.Range(_pitchMin, _pitchMax);
        _footstepSource.volume = _footstepVolume;
        _footstepSource.clip = clip;
        _footstepSource.Play();
    }

    /// <summary>
    /// 이동이 멈췄을 때 재생 중인 발소리를 즉시 중지합니다.
    /// </summary>
    private void StopFootstepIfPlaying()
    {
        if (_footstepSource == null)
            return;

        if (!_footstepSource.isPlaying)
            return;

        if (!IsCurrentFootstepClip(_footstepSource.clip))
            return;

        _footstepSource.Stop();
    }

    /// <summary>
    /// 현재 AudioSource 클립이 발소리 목록에 포함되는지 판정합니다.
    /// </summary>
    private bool IsCurrentFootstepClip(AudioClip currentClip)
    {
        if (currentClip == null || _footstepClips == null)
            return false;

        for (int i = 0; i < _footstepClips.Length; i++)
        {
            if (_footstepClips[i] == currentClip)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 정책에 따라 현재 인스턴스가 SFX를 출력 가능한지 판정합니다.
    /// </summary>
    private bool CanPlaySfx()
    {
        if (!_playOnlyForLocalOwner)
            return true;

        if (_networkObject == null)
            return true;

        return _networkObject.IsOwner;
    }
}
