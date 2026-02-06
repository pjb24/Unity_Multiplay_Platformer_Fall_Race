using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 로컬 플레이어의 카메라 타깃 Transform을 중앙에서 전달하는 버스.
/// - public event 미사용. AddListener/RemoveListener만 제공.
/// - 타깃 변경/해제 시 즉시 통지.
/// </summary>
public static class LocalPlayerTargetBus
{
    private static readonly List<Action<Transform>> _listeners = new();
    private static Transform _current;

    public static Transform Current => _current;

    public static void AddListener(Action<Transform> listener, bool invokeImmediately = true)
    {
        if (listener == null) return;
        if (_listeners.Contains(listener)) return;

        _listeners.Add(listener);

        if (invokeImmediately)
            listener.Invoke(_current);
    }

    public static void RemoveListener(Action<Transform> listener)
    {
        if (listener == null) return;
        _listeners.Remove(listener);
    }

    public static void Publish(Transform target)
    {
        _current = target;

        for (int i = 0; i < _listeners.Count; i++)
            _listeners[i]?.Invoke(_current);
    }

    public static void Clear(Transform expectedCurrent = null)
    {
        if (expectedCurrent != null && _current != expectedCurrent)
            return;

        Publish(null);
    }
}
