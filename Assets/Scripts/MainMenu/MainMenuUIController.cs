using System;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class MainMenuUIController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_InputField _usernameInput;
    [SerializeField] private Button _quickSessionButton;
    [SerializeField] private Button _quitButton;
    [SerializeField] private TMP_Text _statusText;

    [Header("Scene")]
    [SerializeField] private string _lobbySceneName = "Lobby";

    private CancellationTokenSource _cts;

    private void Awake()
    {
        if (_quickSessionButton == null || _statusText == null)
        {
            Debug.LogError("[MainMenuUIController] Missing UI references.");
            enabled = false;
            return;
        }

        _quickSessionButton.onClick.AddListener(OnClickQuickSession);

        if (_quitButton != null)
            _quitButton.onClick.AddListener(OnClickQuit);

        _cts = new CancellationTokenSource();
    }

    private void Start()
    {
        if (QuickSessionContext.Instance == null)
        {
            Debug.LogError("[MainMenuUIController] QuickSessionContext.Instance is null. Put QuickSessionContext in Bootstrap scene.");
            SetStatus("Missing QuickSessionContext");
            SetInteractable(false);
            return;
        }

        string cached = QuickSessionContext.Instance.GetCachedPlayerName();
        if (_usernameInput != null && !string.IsNullOrWhiteSpace(cached))
            _usernameInput.text = cached;

        SetStatus("Ready");
    }

    private void OnDestroy()
    {
        if (_quickSessionButton != null)
            _quickSessionButton.onClick.RemoveListener(OnClickQuickSession);

        if (_quitButton != null)
            _quitButton.onClick.RemoveListener(OnClickQuit);

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnClickQuickSession()
    {
        if (QuickSessionContext.Instance == null)
        {
            Debug.LogError("[MainMenuUIController] QuickSessionContext.Instance is null.");
            SetStatus("Missing QuickSessionContext");
            return;
        }

        if (QuickSessionContext.Instance.IsBusy)
        {
            Debug.LogWarning("[MainMenuUIController] QuickSession fallback: busy.");
            return;
        }

        _ = RunQuickSessionAsync(_cts.Token);
    }

    private async Task RunQuickSessionAsync(CancellationToken ct)
    {
        SetInteractable(false);

        try
        {
            var progress = new Progress<string>(SetStatus);

            string inputName = _usernameInput != null ? _usernameInput.text : null;

            bool ok = await QuickSessionContext.Instance.QuickSessionAsync(inputName, progress, ct);
            if (!ok)
            {
                SetInteractable(true);
                return;
            }

            SceneManager.LoadScene(_lobbySceneName, LoadSceneMode.Single);
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("[MainMenuUIController] QuickSession fallback: canceled.");
            SetStatus("Canceled");
            SetInteractable(true);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MainMenuUIController] QuickSession fallback: failed.\n{ex}");
            SetStatus("Failed. Check console.");
            SetInteractable(true);
        }
    }

    private void OnClickQuit()
    {
#if UNITY_EDITOR
        Debug.LogWarning("[MainMenuUIController] Quit requested (Editor). Stopping play mode.");
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Debug.LogWarning("[MainMenuUIController] Quit requested. Application.Quit()");
        Application.Quit();
#endif
    }

    private void SetInteractable(bool value)
    {
        if (_quickSessionButton != null) _quickSessionButton.interactable = value;
        if (_usernameInput != null) _usernameInput.interactable = value;
        if (_quitButton != null) _quitButton.interactable = value;
    }

    private void SetStatus(string msg)
    {
        if (_statusText != null)
            _statusText.text = msg;
    }
}
