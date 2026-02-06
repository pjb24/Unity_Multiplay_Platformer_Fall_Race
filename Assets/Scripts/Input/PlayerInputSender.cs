using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Owner 클라에서만 입력을 수집하고 서버로 전송한다.
/// - 입력 수집: LocalInputReceiver(MonoBehaviour)가 담당
/// - 이 컴포넌트는 Owner일 때만 입력 리스너를 붙이고, sendHz로 서버 RPC 전송
/// - Gate 닫힘: move=zero, jumpDown=false 를 계속 전송(권장)
/// 
/// [중요] 점프 누락 방지:
/// - jumpDown은 "큐(래치)"로 잡아두고, 실제 전송이 발생한 프레임에만 소비한다.
/// - sendHz 샘플링 사이에 눌렀다 떼도 다음 전송에 포함된다.
/// </summary>
public sealed class PlayerInputSender : NetworkBehaviour
{
    [Header("Refs")]
    [SerializeField] private PlayerInputGate _gate;
    [SerializeField] private LocalInputReceiver _input; // 전용 입력 수신기 (MonoBehaviour)
    [SerializeField] private Transform _cameraTransform;

    [Header("Send Rate")]
    [SerializeField, Range(10, 60)] private int _sendHz = 30;

    private Vector2 _cachedMoveInput;
    // 점프 Down 이벤트는 큐(래치)로 유지
    private bool _jumpQueued;

    private float _sendAccum;
    private int _tick;
    private bool _subscribed;

    private void Awake()
    {
        if (_gate == null)
        {
            Debug.LogWarning("[PlayerInputSender] Fallback 발생: PlayerInputGate is null. Always open으로 간주한다.");
        }

        if (_input == null)
            Debug.LogWarning("[PlayerInputSender] Fallback 발생: LocalInputReceiver is null. 입력 전송 불가.");
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            // Non-owner는 입력 자체를 끈다 (로컬 입력 오작동 방지)
            if (_input != null)
            {
                _input.enabled = false;
            }
            enabled = false;
            return;
        }

        // Owner만 입력 리스너 연결
        TrySubscribeOwnerInput();
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;
        TryUnsubscribeOwnerInput();
    }

    private void OnEnable()
    {
        if (!IsOwner) return;
        TrySubscribeOwnerInput();
    }

    private void OnDisable()
    {
        if (!IsOwner) return;
        TryUnsubscribeOwnerInput();
    }

    private void Update()
    {
        // Owner만 동작
        if (!IsOwner) return;
        if (_input == null) return;

        _tick++;

        // Gate 반영(닫히면 0/false로 강제)
        Vector2 move = GetCameraRelativeMove(_cachedMoveInput);
        bool jumpDown = _jumpQueued; // 큐는 아직 소비하지 않는다

        // Gate 반영
        if (!IsGateOpen())
        {
            move = Vector2.zero;
            jumpDown = false;

            // Gate 닫힘 정책: 큐도 버린다
            _jumpQueued = false;
        }

        // 전송 레이트 제한(너무 자주 보내지 않기)
        float interval = 1f / Mathf.Max(1, _sendHz);
        _sendAccum += Time.unscaledDeltaTime;

        // 매 프레임 보내고 싶으면 여기서 제한 제거하면 됨.
        if (_sendAccum < interval) return;
        _sendAccum = 0f;

        // 여기서 "전송"이 실제로 일어남 -> 이제 큐 소비
        SubmitInputRpc(move, jumpDown, _tick);

        if (jumpDown)
            _jumpQueued = false;
    }

    private void TrySubscribeOwnerInput()
    {
        if (_subscribed) return;

        if (_input == null)
        {
            Debug.LogWarning("[PlayerInputSender] Fallback 발생: LocalInputReceiver missing. Subscribe 불가.");
            return;
        }

        _input.AddMoveListener(OnMove);
        _input.AddJumpListener(OnJumpDown);

        _subscribed = true;

        // 초기값 정리
        _cachedMoveInput = Vector2.zero;
        _jumpQueued = false;
        _sendAccum = 0f;
        _tick = 0;
    }

    private void TryUnsubscribeOwnerInput()
    {
        if (!_subscribed) return;
        if (_input == null)
        {
            _subscribed = false;
            return;
        }

        _input.RemoveMoveListener(OnMove);
        _input.RemoveJumpListener(OnJumpDown);

        _subscribed = false;
    }

    private void OnMove(Vector2 move)
    {
        // 대각선 보정
        if (move.sqrMagnitude > 1f) move.Normalize();
        _cachedMoveInput = move;
    }

    private void OnJumpDown()
    {
        // Down 엣지를 큐로 래치(전송될 때까지 유지)
        _jumpQueued = true;
    }

    private Vector2 GetCameraRelativeMove(Vector2 input)
    {
        if (input == Vector2.zero) return Vector2.zero;

        if (_cameraTransform == null && Camera.main != null)
        {
            _cameraTransform = Camera.main.transform;
        }

        if (_cameraTransform == null)
            return input;

        Vector3 forward = _cameraTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            return input;
        forward.Normalize();

        Vector3 right = _cameraTransform.right;
        right.y = 0f;
        right.Normalize();

        Vector3 worldMove = forward * input.y + right * input.x;
        Vector2 move = new Vector2(worldMove.x, worldMove.z);
        if (move.sqrMagnitude > 1f) move.Normalize();
        return move;
    }

    private bool IsGateOpen()
    {
        if (_gate == null) return true;
        return _gate.IsOpen;
    }

    // Owner만 호출 가능하게 InvokePermission.Owner
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void SubmitInputRpc(Vector2 move, bool jumpDown, int tick, RpcParams rpcParams = default)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerInputSender] SubmitInput fallback 발생: called on non-server.");
            return;
        }

        // 서버에서만 실행됨
        // 소유자 검증(치트/오작동 방지 기본)
        if (rpcParams.Receive.SenderClientId != OwnerClientId)
        {
            Debug.LogWarning($"[PlayerInputSender] Input spoof detected. sender={rpcParams.Receive.SenderClientId}, owner={OwnerClientId}");
            return;
        }

        // 서버 측 저장은 PlayerMotorServer가 담당하게 위임하는 게 깔끔
        // (이 컴포넌트는 입력 전송만 담당)
        var motor = GetComponent<PlayerMotorServer>();
        if (motor == null)
        {
            Debug.LogWarning("[PlayerInputSender] Fallback 발생: PlayerMotorServer missing.");
            return;
        }

        motor.SetInput_Server(move, jumpDown, tick);
    }
}
