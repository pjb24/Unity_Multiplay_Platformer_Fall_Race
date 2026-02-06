using System.Collections;
using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// CinemachineCamera의 TrackingTarget을 로컬 플레이어 타깃으로 자동 세팅.
/// - 타깃이 아직 없으면 폴백 폴링(Warning 로그)으로 복구.
/// </summary>
public sealed class CinemachineTrackingTargetBinder : MonoBehaviour
{
    [SerializeField] private CinemachineCamera _cmCamera;

    [Header("Fallback Polling")]
    [SerializeField] private float _pollIntervalSec = 0.2f;
    [SerializeField] private float _pollTimeoutSec = 10f;

    private Coroutine _pollRoutine;

    private void Awake()
    {
        if (_cmCamera == null)
            _cmCamera = GetComponent<CinemachineCamera>();

        if (_cmCamera == null)
            Debug.LogError("[CinemachineTrackingTargetBinder] _cmCamera is null.");
    }

    private void OnEnable()
    {
        LocalPlayerTargetBus.AddListener(OnTargetChanged);

        if (_pollRoutine != null)
            StopCoroutine(_pollRoutine);

        _pollRoutine = StartCoroutine(PollUntilTargetReady());
    }

    private void OnDisable()
    {
        LocalPlayerTargetBus.RemoveListener(OnTargetChanged);

        if (_pollRoutine != null)
        {
            StopCoroutine(_pollRoutine);
            _pollRoutine = null;
        }
    }

    private void OnTargetChanged(Transform t)
    {
        if (_cmCamera == null)
            return;

        _cmCamera.Follow = t;
    }

    private IEnumerator PollUntilTargetReady()
    {
        if (_cmCamera == null)
            yield break;

        float elapsed = 0f;

        while (LocalPlayerTargetBus.Current == null && elapsed < _pollTimeoutSec)
        {
            Debug.LogWarning("[CinemachineTrackingTargetBinder] Bind fallback 발생: local camera target not ready. polling...");
            yield return new WaitForSeconds(_pollIntervalSec);
            elapsed += _pollIntervalSec;
        }

        var t = LocalPlayerTargetBus.Current;
        if (t == null)
        {
            Debug.LogWarning("[CinemachineTrackingTargetBinder] Bind fallback 발생: timeout. TrackingTarget remains null.");
            yield break;
        }

        _cmCamera.Follow = t;
    }
}
