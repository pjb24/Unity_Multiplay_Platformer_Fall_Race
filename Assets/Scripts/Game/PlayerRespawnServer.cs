using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using System.Collections;

/// <summary>
/// 서버 권위 리스폰/체크포인트 관리.
/// - FallZone 및 Hard Bottom 판정은 서버에서 최종 확정
/// - 플레이어별 마지막 체크포인트 저장
/// - 체크포인트는 Running 상태에서만 갱신(카운트다운/로비에서 미리 밟는 버그 차단)
/// - 스테이지 전환 시 서버가 SetStageStartCheckpoint_Server로 "스테이지 기본 체크포인트"를 즉시 갱신
/// - Running이 아닌 상태에서 리스폰 시 "스테이지 기본 체크포인트"로 이동
/// - Owner 입력 잠금/해제는 [Rpc] (SendTo.Owner)로 단일 타겟 전송
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public sealed class PlayerRespawnServer : NetworkBehaviour
{
    [Header("Checkpoint")]
    [SerializeField] private int _startCheckpointIndex = 1000;

    [Header("Fall Settings")]
    [SerializeField] private float _respawnDelaySec = 1f;
    [SerializeField] private float _hardBottomY = -200f;
    [SerializeField] private float _hardBottomCheckInterval = 0.5f;

    [Header("Input Lock (Owner)")]
    [SerializeField] private bool _lockInputDuringRespawn = true;

    public bool IsRespawning => _isRespawning;

    // last checkpoint (Running에서만 갱신)
    private Vector3 _lastCheckpointPosition;
    private Quaternion _lastCheckpointRotation;
    private int _lastCheckpointIndex;
    private bool _hasCheckpoint;

    // stage default (Running이 아닌 상태에서 리스폰 시 사용)
    private Vector3 _stageDefaultPosition;
    private Quaternion _stageDefaultRotation;
    private int _stageDefaultIndex;
    private bool _hasStageDefault;

    private bool _isRespawning;
    private float _nextHardBottomCheckAt;
    private Coroutine _respawnRoutine;

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            enabled = false;
            return;
        }

        SetInitialCheckpoint_Server();
    }

    private void Update()
    {
        if (!IsServer) return;

        if (Time.time < _nextHardBottomCheckAt)
            return;

        _nextHardBottomCheckAt = Time.time + Mathf.Max(0.05f, _hardBottomCheckInterval);

        if (transform.position.y <= _hardBottomY)
        {
            Debug.LogWarning($"[Respawn] Hard bottom fallback 발생: y={transform.position.y}, hardBottomY={_hardBottomY}");
            TriggerRespawn_Server("hard_bottom");
        }
    }

    /// <summary>
    /// 일반 체크포인트(구간 진행용).
    /// - Running 상태에서만 저장한다.
    /// - Running이 아니면 무시 + Warning (미리 걸어가서 등록되는 버그 차단)
    /// </summary>
    public void SetCheckpoint_Server(Vector3 position, Quaternion rotation, int checkpointIndex)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[Respawn] SetCheckpoint_Server fallback 발생: called on non-server.");
            return;
        }

        if (_isRespawning)
            return;

        var session = GameSessionController.Instance;
        if (session == null)
        {
            Debug.LogWarning($"[Respawn] SetCheckpoint_Server fallback 발생: GameSessionController is null. idx={checkpointIndex}");
            return;
        }

        if (session.State != E_GameSessionState.Running)
        {
            Debug.LogWarning($"[Respawn] Pre-Run checkpoint ignored: state={session.State}, idx={checkpointIndex}");
            return;
        }

        // 진행 방향으로만 갱신
        if (_hasCheckpoint && checkpointIndex <= _lastCheckpointIndex)
            return;

        _lastCheckpointPosition = position;
        _lastCheckpointRotation = rotation;
        _lastCheckpointIndex = checkpointIndex;
        _hasCheckpoint = true;
    }

    /// <summary>
    /// 스테이지 전환 시 서버가 호출해서 "스테이지 기본 체크포인트"를 즉시 갱신한다.
    /// - 전환 직후/카운트다운 상태에서 리스폰이 걸려도 여기로 이동해야 한다.
    /// - 이 함수는 last checkpoint도 같이 덮어쓴다(되감기/미진행 체크포인트 버그 차단).
    /// </summary>
    public void SetStageStartCheckpoint_Server(Vector3 position, Quaternion rotation, int checkpointIndex)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[Respawn] SetStageStartCheckpoint_Server fallback 발생: called on non-server.");
            return;
        }

        if (_isRespawning)
            return;

        // stage default 갱신
        _stageDefaultPosition = position;
        _stageDefaultRotation = rotation;
        _stageDefaultIndex = checkpointIndex;
        _hasStageDefault = true;

        // last도 즉시 갱신(전환 직후 fall/respawn 안전)
        _lastCheckpointPosition = position;
        _lastCheckpointRotation = rotation;
        _lastCheckpointIndex = checkpointIndex;
        _hasCheckpoint = true;
    }

    public void TriggerRespawn_Server(string reason)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[Respawn] TriggerRespawn_Server fallback 발생: called on non-server.");
            return;
        }

        if (_isRespawning)
            return;

        if (_respawnRoutine != null)
            StopCoroutine(_respawnRoutine);

        _respawnRoutine = StartCoroutine(RespawnRoutine_Server(reason));
    }

    private IEnumerator RespawnRoutine_Server(string reason)
    {
        _isRespawning = true;

        SetInputGateOwnerRpc(false, reason);

        ResolveRespawnPose_Server(reason, out Vector3 pos, out Quaternion rot);

        var netTransform = GetComponent<NetworkTransform>();
        if (netTransform != null)
        {
            netTransform.Teleport(pos, rot, transform.localScale);
        }
        else
        {
            Debug.LogWarning("[Respawn] NetworkTransform missing fallback 발생: transform.SetPositionAndRotation 사용.");
            transform.SetPositionAndRotation(pos, rot);
        }

        var motor = GetComponent<PlayerMotorServer>();
        if (motor != null)
        {
            motor.StopImmediately_Server($"respawn:{reason}");
        }
        else
        {
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        SetInputGateOwnerRpc(true, reason);

        if (_respawnDelaySec > 0f)
            yield return new WaitForSeconds(_respawnDelaySec);

        _isRespawning = false;
        _respawnRoutine = null;
    }

    private void ResolveRespawnPose_Server(string reason, out Vector3 pos, out Quaternion rot)
    {
        var session = GameSessionController.Instance;
        if (session == null)
        {
            Debug.LogWarning($"[Respawn] ResolveRespawnPose_Server fallback 발생: GameSessionController is null. reason={reason}");
            ResolveFallbackPose_Server(reason, out pos, out rot);
            return;
        }

        // Running이 아닌 상태면 "스테이지 기본 체크포인트"로 이동
        if (session.State != E_GameSessionState.Running)
        {
            if (_hasStageDefault)
            {
                pos = _stageDefaultPosition;
                rot = _stageDefaultRotation;
                return;
            }

            Debug.LogWarning($"[Respawn] Stage default fallback 발생: _hasStageDefault=false. state={session.State}, reason={reason}");
            ResolveFallbackPose_Server(reason, out pos, out rot);
            return;
        }

        // Running이면 last checkpoint로 이동
        if (_hasCheckpoint)
        {
            pos = _lastCheckpointPosition;
            rot = _lastCheckpointRotation;
            return;
        }

        Debug.LogWarning($"[Respawn] Checkpoint fallback 발생: _hasCheckpoint=false. reason={reason}");
        pos = transform.position;
        rot = transform.rotation;
    }

    private void ResolveFallbackPose_Server(string reason, out Vector3 pos, out Quaternion rot)
    {
        if (_hasStageDefault)
        {
            pos = _stageDefaultPosition;
            rot = _stageDefaultRotation;
            return;
        }

        if (_hasCheckpoint)
        {
            pos = _lastCheckpointPosition;
            rot = _lastCheckpointRotation;
            Debug.LogWarning($"[Respawn] Fallback: stage default missing -> last checkpoint 사용. reason={reason}");
            return;
        }

        pos = transform.position;
        rot = transform.rotation;
        Debug.LogWarning($"[Respawn] Fallback: stage default & checkpoint missing -> current pose 사용. reason={reason}");
    }

    private void SetInitialCheckpoint_Server()
    {
        // 초기 스폰 위치를 stage default + last checkpoint로 같이 잡는다.
        _lastCheckpointPosition = transform.position;
        _lastCheckpointRotation = transform.rotation;
        _lastCheckpointIndex = _startCheckpointIndex;
        _hasCheckpoint = true;

        _stageDefaultPosition = _lastCheckpointPosition;
        _stageDefaultRotation = _lastCheckpointRotation;
        _stageDefaultIndex = _lastCheckpointIndex;
        _hasStageDefault = true;
    }

    private void SetInputGateOwnerRpc(bool open, string reason)
    {
        if (!_lockInputDuringRespawn)
            return;

        // server-only wrapper
        if (!IsServer)
        {
            Debug.LogWarning("[Respawn] SetInputGateOwnerRpc fallback 발생: called on non-server.");
            return;
        }

        // SendTo.Owner + InvokePermission.Server 이라 여기서 그냥 호출하면 됨.
        SetInputGate_ClientRpc(open, reason);
    }

    // NGO 2.8+ 권장: [Rpc] 사용 (ClientRpcParams 대신 SendTo 타겟)
    [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server, Delivery = RpcDelivery.Reliable)]
    private void SetInputGate_ClientRpc(bool open, string reason)
    {
        _ = reason;
        var gate = GetComponent<PlayerInputGate>();
        if (gate == null)
            return;

        if (open)
            gate.Open(E_InputGateReason.Run);
        else
            gate.Close(E_InputGateReason.Respawn);
    }
}
