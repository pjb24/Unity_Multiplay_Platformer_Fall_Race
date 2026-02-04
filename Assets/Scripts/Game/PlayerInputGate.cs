using Unity.Netcode;
using UnityEngine;

public sealed class PlayerInputGate : NetworkBehaviour
{
    [SerializeField] private Behaviour[] _inputBehaviours;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            SetEnabled(false);
            return;
        }

        // 기본은 Lobby에서 입력 OFF
        SetEnabled(false);
    }

    public void SetEnabled(bool enabled)
    {
        if (_inputBehaviours == null) return;

        for (int i = 0; i < _inputBehaviours.Length; i++)
        {
            if (_inputBehaviours[i] == null) continue;
            _inputBehaviours[i].enabled = enabled;
        }
    }
}
