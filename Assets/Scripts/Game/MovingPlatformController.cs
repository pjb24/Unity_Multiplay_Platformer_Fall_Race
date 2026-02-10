using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// 서버 권위 왕복 이동 플랫폼.
/// - waypoint를 따라 ping-pong 이동
/// - 속도/대기시간/시작 위상차(phase offset) 노출
/// - NetworkTransform으로 클라이언트 동기화
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public sealed class MovingPlatformController : NetworkBehaviour
{
    [Header("Path")]
    [SerializeField] private Transform[] _waypoints;
    [SerializeField] private bool _useLocalWaypoints = false;

    [Header("Movement")]
    [SerializeField] private float _moveSpeed = 1.8f;
    [SerializeField] private float _arrivalThreshold = 0.03f;
    [SerializeField] private float _dwellTimeAtPoint = 0.2f;
    [SerializeField] private float _phaseOffsetSec = 0f;

    [Header("Debug")]
    [SerializeField] private bool _drawGizmos = true;

    public Vector3 CurrentVelocity { get; private set; }

    private int _index;
    private int _direction = 1;
    private float _waitUntil;
    private Vector3 _lastPos;

    private void Awake()
    {
        _lastPos = transform.position;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
            return;

        if (_waypoints == null || _waypoints.Length < 2)
        {
            Debug.LogWarning($"[MovingPlatform] {name}: waypoint가 2개 미만이라 이동 비활성화.");
            enabled = false;
            return;
        }

        if (_phaseOffsetSec > 0f)
            _waitUntil = Time.time + _phaseOffsetSec;

        var nt = GetComponent<NetworkTransform>();
        if (nt == null)
        {
            Debug.LogWarning($"[MovingPlatform] {name}: NetworkTransform 없음. 싱글에서는 동작하나 멀티 동기화 품질 저하 가능.");
        }
    }

    private void FixedUpdate()
    {
        if (!IsServer)
            return;

        if (Time.time < _waitUntil)
        {
            CurrentVelocity = Vector3.zero;
            _lastPos = transform.position;
            return;
        }

        Vector3 target = GetWaypointPosition(_index);
        Vector3 from = transform.position;

        float step = Mathf.Max(0.01f, _moveSpeed) * Time.fixedDeltaTime;
        Vector3 next = Vector3.MoveTowards(from, target, step);
        transform.position = next;

        float dist = Vector3.Distance(next, target);
        if (dist <= _arrivalThreshold)
        {
            if (_index == _waypoints.Length - 1)
                _direction = -1;
            else if (_index == 0)
                _direction = 1;

            _index = Mathf.Clamp(_index + _direction, 0, _waypoints.Length - 1);
            _waitUntil = Time.time + Mathf.Max(0f, _dwellTimeAtPoint);
        }

        CurrentVelocity = (transform.position - _lastPos) / Time.fixedDeltaTime;
        _lastPos = transform.position;
    }

    private Vector3 GetWaypointPosition(int index)
    {
        if (_useLocalWaypoints)
            return transform.parent != null
                ? transform.parent.TransformPoint(_waypoints[index].localPosition)
                : transform.TransformPoint(_waypoints[index].localPosition);

        return _waypoints[index].position;
    }

    private void OnDrawGizmos()
    {
        if (!_drawGizmos || _waypoints == null || _waypoints.Length < 2)
            return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < _waypoints.Length - 1; i++)
        {
            if (_waypoints[i] == null || _waypoints[i + 1] == null) continue;

            Vector3 a = _waypoints[i].position;
            Vector3 b = _waypoints[i + 1].position;
            Gizmos.DrawLine(a, b);

            Vector3 mid = Vector3.Lerp(a, b, 0.5f);
            Vector3 dir = (b - a).normalized;
            Gizmos.DrawRay(mid, dir * 0.4f);
        }
    }
}
