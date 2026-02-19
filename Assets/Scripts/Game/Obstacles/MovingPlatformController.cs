using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// 서버 권위 왕복 이동 플랫폼.
/// - waypoint를 따라 ping-pong 이동
/// - 속도/대기시간/시작 위상차(phase offset) 노출
/// - NetworkTransform으로 클라이언트 동기화
/// - Rigidbody.MovePosition 기반 고정틱 이동 및 델타 제공
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
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

    /// <summary>현재 틱에서 계산된 플랫폼 속도입니다.</summary>
    public Vector3 CurrentVelocity { get; private set; }

    /// <summary>현재 틱에서 계산된 플랫폼 이동 델타입니다.</summary>
    public Vector3 CurrentDelta { get; private set; }

    /// <summary>플랫폼이 현재 접촉 중이라고 서버에서 판정한 플레이어 집합입니다.</summary>
    private readonly HashSet<PlayerMotorServer> _contactPlayers = new();

    /// <summary>플랫폼 이동 계산에 사용하는 Rigidbody 참조입니다.</summary>
    private Rigidbody _rb;

    /// <summary>현재 목표 waypoint 인덱스입니다.</summary>
    private int _index;
    /// <summary>ping-pong 이동 방향(1 또는 -1)입니다.</summary>
    private int _direction = 1;
    /// <summary>도착 후 대기 종료 시각입니다.</summary>
    private float _waitUntil;
    /// <summary>직전 틱 위치(델타 계산용)입니다.</summary>
    private Vector3 _previousPosition;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        _previousPosition = _rb.position;
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

        Vector3 from = _rb.position;
        Vector3 next = from;

        if (Time.time < _waitUntil)
        {
            // 대기 프레임에서는 위치를 유지합니다.
            next = from;
        }
        else
        {
            Vector3 target = GetWaypointPosition(_index);
            float step = Mathf.Max(0.01f, _moveSpeed) * Time.fixedDeltaTime;
            next = Vector3.MoveTowards(from, target, step);

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
        }

        _rb.MovePosition(next);

        CurrentDelta = next - _previousPosition;
        CurrentVelocity = CurrentDelta / Time.fixedDeltaTime;
        _previousPosition = next;
    }

    /// <summary>
    /// 서버가 관리하는 플랫폼 접촉 플레이어를 등록합니다.
    /// </summary>
    public void RegisterContactPlayer(PlayerMotorServer player)
    {
        if (!IsServer || player == null)
            return;

        _contactPlayers.Add(player);
    }

    /// <summary>
    /// 서버가 관리하는 플랫폼 접촉 플레이어를 해제합니다.
    /// </summary>
    public void UnregisterContactPlayer(PlayerMotorServer player)
    {
        if (!IsServer || player == null)
            return;

        _contactPlayers.Remove(player);
    }

    /// <summary>
    /// 지정 플레이어가 현재 플랫폼과 접촉 중인지 서버 기준으로 반환합니다.
    /// </summary>
    public bool IsPlayerInContact(PlayerMotorServer player)
    {
        return player != null && _contactPlayers.Contains(player);
    }

    private Vector3 GetWaypointPosition(int index)
    {
        if (_useLocalWaypoints)
            return transform.parent != null
                ? transform.parent.TransformPoint(_waypoints[index].localPosition)
                : transform.TransformPoint(_waypoints[index].localPosition);

        return _waypoints[index].position;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer)
            return;

        PlayerMotorServer player = collision.collider.GetComponentInParent<PlayerMotorServer>();
        if (player != null)
            RegisterContactPlayer(player);
    }

    private void OnCollisionExit(Collision collision)
    {
        if (!IsServer)
            return;

        PlayerMotorServer player = collision.collider.GetComponentInParent<PlayerMotorServer>();
        if (player != null)
            UnregisterContactPlayer(player);
    }

    private void OnDisable()
    {
        _contactPlayers.Clear();
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
