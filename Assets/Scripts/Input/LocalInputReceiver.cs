using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 로컬 입력 전용 수신기
/// - InputActionAsset + action path 문자열("Player/Move")로 액션을 찾는다.
/// </summary>
public sealed class LocalInputReceiver : MonoBehaviour
{
    [Header("Input Actions")]
    [SerializeField] private InputActionAsset _asset;

    [Header("Action Paths (Map/Action)")]
    [SerializeField] private string _movePath = "Player/Move"; // Value(Vector2)
    [SerializeField] private string _jumpPath = "Player/Jump"; // Button

    private InputAction _moveAction;
    private InputAction _jumpAction;

    // listeners (외부 노출 금지)
    private Action<Vector2> _onMove;
    private Action _onJump;

    private bool _initialized;

    #region Unity Lifecycle

    private void Awake()
    {
        ResolveActionsOrFallback();
        BindActions();
        _initialized = true;
    }

    private void OnEnable()
    {
        if (!_initialized) return;

        if (_moveAction != null) _moveAction.Enable();
        if (_jumpAction != null) _jumpAction.Enable();
    }

    private void OnDisable()
    {
        if (!_initialized) return;

        if (_moveAction != null) _moveAction.Disable();
        if (_jumpAction != null) _jumpAction.Disable();
    }

    private void OnDestroy()
    {
        UnbindActions();
    }

    #endregion

    #region Public Listener API

    public void AddMoveListener(Action<Vector2> listener)
    {
        _onMove += listener;
    }

    public void RemoveMoveListener(Action<Vector2> listener)
    {
        _onMove -= listener;
    }

    public void AddJumpListener(Action listener)
    {
        _onJump += listener;
    }

    public void RemoveJumpListener(Action listener)
    {
        _onJump -= listener;
    }

    #endregion

    #region Internal

    private void ResolveActionsOrFallback()
    {
        if (_asset == null)
        {
            Debug.LogWarning("[LocalInputReceiver_ByAsset] Fallback 발생: InputActionAsset is null. 입력 수신 불가.");
            _moveAction = null;
            _jumpAction = null;
            return;
        }

        _moveAction = FindActionOrWarn(_movePath, "Move");
        _jumpAction = FindActionOrWarn(_jumpPath, "Jump");
    }

    private InputAction FindActionOrWarn(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Debug.LogWarning($"[LocalInputReceiver_ByAsset] Fallback 발생: {label} path is empty.");
            return null;
        }

        // "Map/Action" 형태 권장. findActionMap(action)도 가능하지만 여기선 명확히 분리한다.
        int slash = path.IndexOf('/');
        if (slash <= 0 || slash >= path.Length - 1)
        {
            Debug.LogWarning($"[LocalInputReceiver_ByAsset] Fallback 발생: {label} path format invalid. expected \"Map/Action\" but got \"{path}\"");
            return null;
        }

        string mapName = path.Substring(0, slash);
        string actionName = path.Substring(slash + 1);

        var map = _asset.FindActionMap(mapName, throwIfNotFound: false);
        if (map == null)
        {
            Debug.LogWarning($"[LocalInputReceiver_ByAsset] Fallback 발생: ActionMap not found. map=\"{mapName}\" ({label})");
            return null;
        }

        var action = map.FindAction(actionName, throwIfNotFound: false);
        if (action == null)
        {
            Debug.LogWarning($"[LocalInputReceiver_ByAsset] Fallback 발생: Action not found. map=\"{mapName}\", action=\"{actionName}\" ({label})");
            return null;
        }

        return action;
    }

    private void BindActions()
    {
        if (_moveAction != null)
        {
            _moveAction.performed += OnMovePerformed;
            _moveAction.canceled += OnMoveCanceled;
        }

        if (_jumpAction != null)
        {
            _jumpAction.performed += OnJumpPerformed;
        }
    }

    private void UnbindActions()
    {
        if (_moveAction != null)
        {
            _moveAction.performed -= OnMovePerformed;
            _moveAction.canceled -= OnMoveCanceled;
        }

        if (_jumpAction != null)
        {
            _jumpAction.performed -= OnJumpPerformed;
        }
    }

    private void OnMovePerformed(InputAction.CallbackContext ctx)
    {
        _onMove?.Invoke(ctx.ReadValue<Vector2>());
    }

    private void OnMoveCanceled(InputAction.CallbackContext ctx)
    {
        _onMove?.Invoke(Vector2.zero);
    }

    private void OnJumpPerformed(InputAction.CallbackContext ctx)
    {
        // Button Down 엣지만 전달
        if (ctx.phase != InputActionPhase.Performed)
            return;

        _onJump?.Invoke();
    }

    #endregion
}
