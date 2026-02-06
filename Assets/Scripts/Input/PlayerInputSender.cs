using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Owner 클라에서만 입력을 수집하고 서버로 전송한다.
/// - Gate 닫힘: move=Vector2.zero, jumpDown=false 를 계속 전송(권장)
/// - Jump는 엣지(Down)만 보낸다.
/// </summary>
public sealed class PlayerInputSender : NetworkBehaviour
{
    [Header("Refs")]
    [SerializeField] private PlayerInputGate _gate; // 기존 Gate 참조 (API는 아래 가정)

    [Header("Send Rate")]
    [SerializeField, Range(10, 60)] private int _sendHz = 30;

    private float _sendAccum;
    private int _tick;
    private bool _prevJumpHeld;

    private void Awake()
    {
        if (_gate == null)
        {
            Debug.LogWarning("[PlayerInputSender] Fallback 발생: PlayerInputGate is null. Always open으로 간주한다.");
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }
    }

    private void Update()
    {
        // Owner만 동작
        if (!IsOwner) return;

        _tick++;

        // 입력 수집
        Vector2 move = ReadMove();
        bool jumpHeld = ReadJumpHeld();
        bool jumpDown = jumpHeld && !_prevJumpHeld;
        _prevJumpHeld = jumpHeld;

        // Gate 반영
        bool gateOpen = IsGateOpen();
        if (!gateOpen)
        {
            move = Vector2.zero;
            jumpDown = false;
        }

        // 전송 레이트 제한(너무 자주 보내지 않기)
        float interval = 1f / Mathf.Max(1, _sendHz);
        _sendAccum += Time.unscaledDeltaTime;

        // 매 프레임 보내고 싶으면 여기서 제한 제거하면 됨.
        if (_sendAccum < interval) return;
        _sendAccum = 0f;

        SubmitInputServerRpc(move, jumpDown, _tick);
    }

    private Vector2 ReadMove()
    {
        float x = 0f;
        float y = 0f;

        if (Keyboard.current.aKey.isPressed) x -= 1f;
        if (Keyboard.current.dKey.isPressed) x += 1f;
        if (Keyboard.current.sKey.isPressed) y -= 1f;
        if (Keyboard.current.wKey.isPressed) y += 1f;

        Vector2 v = new Vector2(x, y);
        // 대각선 속도 보정(정규화)
        if (v.sqrMagnitude > 1f) v.Normalize();
        return v;
    }

    private bool ReadJumpHeld()
    {
        return Keyboard.current.spaceKey.isPressed;
    }

    private bool IsGateOpen()
    {
        if (_gate == null) return true;

        // Gate API 가정: _gate.IsOpen bool
        // 네 Gate가 다르면 이 함수만 바꿔라.
        bool open = _gate.IsOpen;

        // Gate가 “상태 꼬임”을 내부에서 검출한다면,
        // 거기서 Warning을 찍게 하거나 여기서 추가로 찍어라.
        return open;
    }

    [ServerRpc]
    private void SubmitInputServerRpc(Vector2 move, bool jumpDown, int tick, ServerRpcParams rpcParams = default)
    {
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
