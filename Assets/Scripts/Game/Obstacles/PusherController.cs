using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Push Block Zone용 서버 권위 푸셔.
/// - Left/Right 기준점 왕복 이동
/// - 충돌 판정 및 AddForce(Impulse)는 서버에서만 수행
/// - 플레이어별 쿨다운 적용
/// - 과도한 수직 튐 방지(y 성분 클램프)
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Collider))]
public sealed class PusherController : NetworkBehaviour
{
    private enum E_StartSide
    {
        Left = 0,
        Right = 1,
    }

    [Header("Path")]
    [SerializeField] private Transform _leftPoint;
    [SerializeField] private Transform _rightPoint;
    [SerializeField] private E_StartSide _startSide = E_StartSide.Left;

    [Header("Movement")]
    [SerializeField] private float _moveSpeed = 2.4f;
    [SerializeField] private float _arrivalThreshold = 0.02f;
    [SerializeField] private bool _lockRotationAxes = true;

    [Header("Push")]
    [SerializeField] private float _pushImpulse = 10f;
    [SerializeField, Range(0.3f, 0.8f)] private float _pushCooldownPerPlayer = 0.5f;
    [SerializeField] private float _maxUpwardY = 2f;
    [SerializeField] private float _knockbackControlLockSec = 0.2f;

    [Header("Hit VFX")]
    // 플레이어 충돌 시 생성할 파티클 프리팹.
    [SerializeField] private GameObject _hitParticlePrefab;
    // 파티클이 벽/바닥에 묻히지 않도록 위로 띄우는 오프셋.
    [SerializeField] private float _hitParticleSpawnYOffset = 0.15f;
    // 파티클 시스템이 없을 때 사용할 기본 제거 지연 시간.
    [SerializeField] private float _hitParticleFallbackDestroyDelay = 2f;

    [Header("Debug")]
    [SerializeField] private bool _drawGizmos = true;
    [SerializeField] private bool _verboseLog = false;

    [Header("Events")]
    [SerializeField] private UnityEvent _onPlayerPushed;

    private readonly Dictionary<ulong, float> _nextPushAllowedAt = new();

    private Vector3 _targetPosition;
    private int _travelDirection = 1;
    private bool _movingToRight;

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
            return;

        if (_leftPoint == null || _rightPoint == null)
        {
            Debug.LogWarning($"[Pusher] {name}: Left/Right point가 누락되어 이동을 중단합니다.");
            enabled = false;
            return;
        }

        _movingToRight = _startSide == E_StartSide.Left;
        _targetPosition = _movingToRight ? _rightPoint.position : _leftPoint.position;
        transform.position = _movingToRight ? _leftPoint.position : _rightPoint.position;

        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            if (_lockRotationAxes)
                rb.constraints = RigidbodyConstraints.FreezeRotation;
        }

        var nt = GetComponent<NetworkTransform>();
        if (nt == null)
            Debug.LogWarning($"[Pusher] {name}: NetworkTransform이 없어 클라 보간 품질이 낮아질 수 있습니다.");
    }

    private void FixedUpdate()
    {
        if (!IsServer)
            return;

        Vector3 current = transform.position;
        float step = Mathf.Max(0.01f, _moveSpeed) * Time.fixedDeltaTime;
        Vector3 next = Vector3.MoveTowards(current, _targetPosition, step);
        transform.position = next;

        if (Vector3.Distance(next, _targetPosition) <= _arrivalThreshold)
        {
            _movingToRight = !_movingToRight;
            _targetPosition = _movingToRight ? _rightPoint.position : _leftPoint.position;
            _travelDirection = _movingToRight ? 1 : -1;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        TryPushPlayer_Server(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        TryPushPlayer_Server(collision);
    }

    private void TryPushPlayer_Server(Collision collision)
    {
        if (!IsServer)
            return;

        var playerNetObj = collision.collider.GetComponentInParent<NetworkObject>();
        if (playerNetObj == null || !playerNetObj.CompareTag("Player"))
            return;

        if (!CanPushNow_Server(playerNetObj.OwnerClientId))
            return;

        var rb = collision.rigidbody != null
            ? collision.rigidbody
            : collision.collider.GetComponentInParent<Rigidbody>();

        if (rb == null)
            return;

        Vector3 dir = ResolvePushDirection(collision, rb.worldCenterOfMass);
        Vector3 impulse = dir * Mathf.Max(0f, _pushImpulse);

        var motor = playerNetObj.GetComponent<PlayerMotorServer>();
        if (motor != null)
        {
            motor.ApplyKnockback_Server(impulse, _knockbackControlLockSec);
            // 푸셔 피격이 확정된 오너에게 충돌 SFX 재생을 요청합니다.
            motor.PlayObstacleHitSfx_Server();
        }
        else
            rb.AddForce(impulse, ForceMode.Impulse);

        _nextPushAllowedAt[playerNetObj.OwnerClientId] = Time.time + Mathf.Max(0.01f, _pushCooldownPerPlayer);
        SpawnHitParticle(collision);
        _onPlayerPushed?.Invoke();

        if (_verboseLog)
            Debug.Log($"[Pusher] Push applied -> player={playerNetObj.OwnerClientId}, impulse={_pushImpulse}, dir={dir}");
    }

    private bool CanPushNow_Server(ulong ownerClientId)
    {
        if (!_nextPushAllowedAt.TryGetValue(ownerClientId, out float readyAt))
            return true;

        return Time.time >= readyAt;
    }

    private Vector3 ResolvePushDirection(Collision collision, Vector3 playerCenter)
    {
        Vector3 pushDir = Vector3.right * _travelDirection;

        if (collision.contactCount > 0)
        {
            ContactPoint contact = collision.GetContact(0);
            if (contact.normal.sqrMagnitude > 0.0001f)
                pushDir = -contact.normal.normalized;
        }

        if (pushDir.sqrMagnitude < 0.0001f)
            pushDir = (playerCenter - transform.position).normalized;

        pushDir.y = Mathf.Clamp(pushDir.y, -0.2f, Mathf.Max(0f, _maxUpwardY));

        Vector3 horizontal = new Vector3(pushDir.x, 0f, pushDir.z);
        if (horizontal.sqrMagnitude < 0.0001f)
            horizontal = Vector3.right * _travelDirection;

        horizontal.Normalize();
        float y = Mathf.Clamp(pushDir.y, -0.2f, Mathf.Max(0f, _maxUpwardY));
        Vector3 result = (horizontal + Vector3.up * y).normalized;

        return result;
    }

    /// <summary>
    /// 플레이어와 충돌한 지점에 파티클 프리팹을 Instantiate 한다.
    /// </summary>
    private void SpawnHitParticle(Collision collision)
    {
        if (_hitParticlePrefab == null)
            return;

        Vector3 spawnPosition = collision.contactCount > 0
            ? collision.GetContact(0).point
            : collision.collider.bounds.center;
        spawnPosition += Vector3.up * Mathf.Max(0f, _hitParticleSpawnYOffset);

        Quaternion spawnRotation = collision.contactCount > 0
            ? Quaternion.LookRotation(-collision.GetContact(0).normal)
            : Quaternion.identity;

        // 생성된 파티클 인스턴스를 저장해 재생 종료 후 제거한다.
        GameObject particleInstance = Instantiate(_hitParticlePrefab, spawnPosition, spawnRotation);
        ScheduleParticleDestroy(particleInstance);
    }

    /// <summary>
    /// 파티클 시스템의 재생 길이를 계산해 인스턴스를 자동 제거한다.
    /// </summary>
    private void ScheduleParticleDestroy(GameObject particleInstance)
    {
        if (particleInstance == null)
            return;

        // 파티클 컴포넌트가 없을 때 사용할 제거 대기 시간.
        float destroyDelay = Mathf.Max(0.1f, _hitParticleFallbackDestroyDelay);
        // 인스턴스 하위 포함 파티클 목록.
        ParticleSystem[] particleSystems = particleInstance.GetComponentsInChildren<ParticleSystem>();

        if (particleSystems.Length > 0)
        {
            destroyDelay = 0f;
            foreach (ParticleSystem particleSystem in particleSystems)
            {
                // 개별 파티클 시스템의 최대 재생 시간(지속시간 + 수명)을 누적 반영한다.
                var main = particleSystem.main;
                float maxStartLifetime = main.startLifetime.constantMax;
                float systemDuration = main.duration + maxStartLifetime;
                destroyDelay = Mathf.Max(destroyDelay, systemDuration);
            }

            destroyDelay += 0.1f;
        }

        Destroy(particleInstance, destroyDelay);
    }

    private void OnDrawGizmos()
    {
        if (!_drawGizmos || _leftPoint == null || _rightPoint == null)
            return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(_leftPoint.position, _rightPoint.position);
        Gizmos.DrawSphere(_leftPoint.position, 0.15f);
        Gizmos.DrawSphere(_rightPoint.position, 0.15f);
    }
}
