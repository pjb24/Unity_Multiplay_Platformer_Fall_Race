using System;
using Unity.Netcode;
using UnityEngine;

public enum E_InputGateReason
{
    Unknown = 0,
    Lobby = 10,
    Countdown = 20,
    Run = 30,
    Goal = 40,
    Result = 50,
    Respawn = 60,
    DebugForce = 90,
}

public sealed class PlayerInputGate : NetworkBehaviour
{
    [Header("Input Behaviours (Owner only)")]
    [SerializeField] private Behaviour[] _inputBehaviours;

    [Header("Options")]
    [SerializeField] private bool _warnIfNullBehaviours = true;

    public bool IsOpen => _isOpen;
    public E_InputGateReason CurrentReason => _reason;

    private bool _isOpen;
    private E_InputGateReason _reason;

    // 이벤트 직접 노출 금지 -> Listener 패턴
    private Action<bool, E_InputGateReason> _onGateChanged;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            // Owner만 입력을 다룸
            SetEnabled_Internal(false, E_InputGateReason.Unknown, silent: true);
            return;
        }

        // 기본은 Lobby에서 입력 OFF
        SetEnabled(true, E_InputGateReason.Lobby);
    }

    public override void OnNetworkDespawn()
    {
        // 리스너 누수 방지
        _onGateChanged = null;
    }

    public void AddListener(Action<bool, E_InputGateReason> listener)
    {
        if (listener == null) return;
        _onGateChanged += listener;
    }

    public void RemoveListener(Action<bool, E_InputGateReason> listener)
    {
        if (listener == null) return;
        _onGateChanged -= listener;
    }

    /// <summary>
    /// Gate를 연다(입력 ON).
    /// Owner만 호출 가능.
    /// </summary>
    public void Open(E_InputGateReason reason)
    {
        SetEnabled(true, reason);
    }

    /// <summary>
    /// Gate를 닫는다(입력 OFF).
    /// Owner만 호출 가능.
    /// </summary>
    public void Close(E_InputGateReason reason)
    {
        SetEnabled(false, reason);
    }

    /// <summary>
    /// 입력 Behaviour들을 enable/disable 한다.
    /// - Owner가 아니면 Warning + 무시(무음 금지)
    /// - 중복 호출은 무시하되, reason 충돌 시 Warning
    /// </summary>
    public void SetEnabled(bool enabled, E_InputGateReason reason)
    {
        if (!IsSpawned)
        {
            Debug.LogWarning("[PlayerInputGate] Fallback 발생: not spawned yet. call ignored.");
            return;
        }

        if (!IsOwner)
        {
            Debug.LogWarning($"[PlayerInputGate] Fallback 발생: non-owner tried to SetEnabled. enabled={enabled}, reason={reason}");
            return;
        }

        SetEnabled_Internal(enabled, reason, silent: false);
    }

    /// <summary>
    /// 네트워크 꼬임/디버그용 강제 잠금.
    /// Owner가 아니면 Warning + 무시.
    /// </summary>
    public void ForceLock(string message = null)
    {
        if (!IsSpawned)
        {
            Debug.LogWarning("[PlayerInputGate] Fallback 발생: ForceLock called before spawn.");
            return;
        }

        if (!IsOwner)
        {
            Debug.LogWarning("[PlayerInputGate] Fallback 발생: non-owner tried to ForceLock.");
            return;
        }

        if (!string.IsNullOrEmpty(message))
            Debug.LogWarning($"[PlayerInputGate] ForceLock: {message}");

        SetEnabled_Internal(false, E_InputGateReason.DebugForce, silent: false);
    }

    private void SetEnabled_Internal(bool enabled, E_InputGateReason reason, bool silent)
    {
        if (_inputBehaviours == null)
        {
            if (!silent)
                Debug.LogWarning("[PlayerInputGate] Fallback 발생: _inputBehaviours is null.");
            _isOpen = enabled;
            _reason = reason;
            return;
        }

        if (_isOpen == enabled)
        {
            if (_reason != reason)
            {
                if (!silent)
                    Debug.LogWarning($"[PlayerInputGate] Fallback 발생: duplicate SetEnabled with different reason. state={enabled}, from={_reason}, to={reason}");

                // 상태는 동일해도 reason은 업데이트(중요)
                _reason = reason;
                _onGateChanged?.Invoke(_isOpen, _reason);
            }
            return;
        }

        for (int i = 0; i < _inputBehaviours.Length; i++)
        {
            var b = _inputBehaviours[i];
            if (b == null)
            {
                if (_warnIfNullBehaviours && !silent)
                    Debug.LogWarning($"[PlayerInputGate] Fallback 발생: _inputBehaviours[{i}] is null.");
                continue;
            }

            if (b is PlayerInputSender)
                continue;

            b.enabled = enabled;
        }

        _isOpen = enabled;
        _reason = reason;

        _onGateChanged?.Invoke(_isOpen, _reason);
    }
}
