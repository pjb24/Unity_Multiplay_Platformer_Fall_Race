using System.Collections;
using UnityEngine;

/// <summary>
/// MainMenu/Lobby/Running BGM를 단일 AudioSource로 관리하는 전역 매니저입니다.
/// </summary>
public sealed class BgmManager : MonoBehaviour
{
    /// <summary>씬 전환 이후에도 유지되는 싱글톤 인스턴스입니다.</summary>
    private static BgmManager _instance;

    [Header("BGM Clips")]
    /// <summary>MainMenu 상태에서 재생할 BGM 클립입니다.</summary>
    [SerializeField] private AudioClip _mainMenuClip;
    /// <summary>Lobby 상태에서 재생할 BGM 클립입니다.</summary>
    [SerializeField] private AudioClip _lobbyClip;
    /// <summary>Running 상태에서 재생할 BGM 클립입니다.</summary>
    [SerializeField] private AudioClip _runningClip;

    [Header("Volume")]
    /// <summary>BGM 기본 볼륨 값입니다.</summary>
    [SerializeField, Range(0f, 1f)] private float _defaultVolume = 0.6f;
    /// <summary>마스터 볼륨 연동을 위한 가중치 값입니다.</summary>
    [SerializeField, Range(0f, 1f)] private float _masterVolumeMultiplier = 1f;

    [Header("Transition")]
    /// <summary>BGM 전환 시 페이드 시간을 초 단위로 정의합니다.</summary>
    [SerializeField, Min(0f)] private float _fadeDurationSeconds = 0.2f;

    /// <summary>BGM을 실제 출력하는 AudioSource 컴포넌트입니다.</summary>
    private AudioSource _audioSource;
    /// <summary>현재 재생 중인 트랙 식별자입니다.</summary>
    private BgmTrack _currentTrack = BgmTrack.None;
    /// <summary>실행 중인 페이드 코루틴 참조입니다.</summary>
    private Coroutine _fadeRoutine;

    /// <summary>전역 인스턴스입니다. 없으면 자동 생성합니다.</summary>
    public static BgmManager Instance
    {
        get
        {
            if (_instance == null)
            {
                CreateManagerGameObject();
            }

            return _instance;
        }
    }

    /// <summary>매니저 존재를 보장하기 위한 유틸리티 함수입니다.</summary>
    public static void EnsureExists()
    {
        _ = Instance;
    }

    /// <summary>Awake에서 중복 인스턴스를 제거하고 AudioSource를 준비합니다.</summary>
    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        InitializeAudioSource();
    }

    /// <summary>MainMenu BGM 재생 요청을 처리합니다.</summary>
    public void PlayMainMenu()
    {
        PlayTrack(BgmTrack.MainMenu, _mainMenuClip);
    }

    /// <summary>Lobby BGM 재생 요청을 처리합니다.</summary>
    public void PlayLobby()
    {
        PlayTrack(BgmTrack.Lobby, _lobbyClip);
    }

    /// <summary>Running BGM 재생 요청을 처리합니다.</summary>
    public void PlayRunning()
    {
        PlayTrack(BgmTrack.Running, _runningClip);
    }

    /// <summary>현재 BGM 재생을 정지하고 상태를 초기화합니다.</summary>
    public void Stop()
    {
        if (_fadeRoutine != null)
        {
            StopCoroutine(_fadeRoutine);
            _fadeRoutine = null;
        }

        if (_audioSource == null)
        {
            Debug.LogWarning("[BGM] Stop fallback 발생: AudioSource가 없습니다.");
            return;
        }

        _audioSource.Stop();
        _audioSource.clip = null;
        _currentTrack = BgmTrack.None;
    }

    /// <summary>옵션 메뉴 연동을 위한 마스터 볼륨 배율을 반영합니다.</summary>
    public void SetMasterVolumeMultiplier(float multiplier)
    {
        _masterVolumeMultiplier = Mathf.Clamp01(multiplier);
        UpdateOutputVolume();
    }

    /// <summary>트랙 전환 요청을 처리하며 동일 트랙 중복 재생을 방지합니다.</summary>
    private void PlayTrack(BgmTrack nextTrack, AudioClip clip)
    {
        if (_audioSource == null)
        {
            Debug.LogWarning("[BGM] Play fallback 발생: AudioSource가 없습니다.");
            return;
        }

        if (clip == null)
        {
            Debug.LogWarning($"[BGM] {nextTrack} 클립이 할당되지 않았습니다.");
            return;
        }

        if (_currentTrack == nextTrack && _audioSource.isPlaying)
        {
            return;
        }

        if (_fadeRoutine != null)
        {
            StopCoroutine(_fadeRoutine);
        }

        _fadeRoutine = StartCoroutine(FadeToTrackRoutine(nextTrack, clip));
    }

    /// <summary>두 단계 페이드(아웃/인)로 트랙을 전환합니다.</summary>
    private IEnumerator FadeToTrackRoutine(BgmTrack nextTrack, AudioClip clip)
    {
        float targetVolume = GetTargetVolume();

        if (_audioSource.isPlaying && _fadeDurationSeconds > 0f)
        {
            yield return FadeRoutine(_audioSource.volume, 0f, _fadeDurationSeconds);
        }

        _audioSource.clip = clip;
        _audioSource.loop = true;
        _audioSource.Play();

        if (_fadeDurationSeconds > 0f)
        {
            yield return FadeRoutine(0f, targetVolume, _fadeDurationSeconds);
        }
        else
        {
            _audioSource.volume = targetVolume;
        }

        _currentTrack = nextTrack;
        _fadeRoutine = null;
    }

    /// <summary>지정된 시간 동안 볼륨을 선형 보간합니다.</summary>
    private IEnumerator FadeRoutine(float start, float end, float duration)
    {
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            _audioSource.volume = Mathf.Lerp(start, end, t);
            yield return null;
        }

        _audioSource.volume = end;
    }

    /// <summary>AudioSource 초기 설정을 적용합니다.</summary>
    private void InitializeAudioSource()
    {
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            _audioSource = gameObject.AddComponent<AudioSource>();
        }

        _audioSource.playOnAwake = false;
        _audioSource.loop = true;
        UpdateOutputVolume();
    }

    /// <summary>현재 기본 볼륨과 마스터 배율을 합산해 최종 볼륨을 갱신합니다.</summary>
    private void UpdateOutputVolume()
    {
        if (_audioSource == null)
        {
            return;
        }

        _audioSource.volume = GetTargetVolume();
    }

    /// <summary>최종 출력 볼륨을 계산합니다.</summary>
    private float GetTargetVolume()
    {
        return Mathf.Clamp01(_defaultVolume * _masterVolumeMultiplier);
    }

    /// <summary>씬에 매니저가 없을 때 자동 생성합니다.</summary>
    private static void CreateManagerGameObject()
    {
        BgmManager existing = FindFirstObjectByType<BgmManager>();
        if (existing != null)
        {
            _instance = existing;
            return;
        }

        GameObject managerObject = new GameObject("BgmManager");
        _instance = managerObject.AddComponent<BgmManager>();
    }

    /// <summary>BGM 분류를 위한 내부 트랙 식별자입니다.</summary>
    private enum BgmTrack
    {
        None = 0,
        MainMenu = 1,
        Lobby = 2,
        Running = 3,
    }
}
