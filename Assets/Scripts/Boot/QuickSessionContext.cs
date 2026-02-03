using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;

public sealed class QuickSessionContext : MonoBehaviour
{
    [Header("Config")]
    [SerializeField] private int _maxPlayers = 4;
    public int MaxPlayers => _maxPlayers;
    [SerializeField] private bool _isPrivate = false;

    [Header("Timeouts (seconds)")]
    [SerializeField] private float _quickJoinTimeoutSeconds = 3f;
    [SerializeField] private float _createLobbyTimeoutSeconds = 3f;
    [SerializeField] private float _servicesInitTimeoutSeconds = 3f;

    private CancellationTokenSource _cts;
    private bool _isBusy;

    public static QuickSessionContext Instance { get; private set; }

    public Lobby CurrentLobby { get; private set; }
    public bool IsHost { get; private set; }
    public string PlayerName { get; private set; }

    public bool IsBusy => _isBusy;

    private const string PrefKeyPlayerName = "player_name";

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[QuickSessionContext] Duplicate detected. Destroying.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        CancelRunning();
    }

    public void Configure(int maxPlayers, bool isPrivate, float servicesTimeout, float quickJoinTimeout, float createTimeout)
    {
        _maxPlayers = Mathf.Max(1, maxPlayers);
        _isPrivate = isPrivate;

        _servicesInitTimeoutSeconds = Mathf.Max(0f, servicesTimeout);
        _quickJoinTimeoutSeconds = Mathf.Max(0f, quickJoinTimeout);
        _createLobbyTimeoutSeconds = Mathf.Max(0f, createTimeout);
    }

    public void CancelRunning()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }

    public void ClearLobbyContext()
    {
        CurrentLobby = null;
        IsHost = false;
        PlayerName = null;
    }

    public string GetCachedPlayerName()
    {
        return PlayerPrefs.GetString(PrefKeyPlayerName, string.Empty);
    }

    /// <summary>
    /// inputName이 비어있으면 Generate해서 PlayerName으로 확정하고 QuickJoin->Create 폴백 수행.
    /// status는 UI가 넘겨준 progress로만 보고한다. (UI 참조 없음)
    /// </summary>
    public async Task<bool> QuickSessionAsync(string inputName, IProgress<string> status, CancellationToken externalCt)
    {
        if (_isBusy)
        {
            Debug.LogWarning("[QuickSessionContext] QuickSession fallback: already running.");
            status?.Report("Busy");
            return false;
        }

        _isBusy = true;
        CancelRunning();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        try
        {
            status?.Report("Initializing services...");
            await EnsureSignedInAsync(_cts.Token);

            PlayerName = GetOrGeneratePlayerName(inputName);
            status?.Report($"Signed in. Name={PlayerName}\nFinding session...");

            Lobby lobby = await TryQuickJoinAsync(PlayerName, _cts.Token);

            if (lobby == null)
            {
                status?.Report("No available session. Creating lobby...");
                lobby = await CreateLobbyAsync(PlayerName, _cts.Token);
                IsHost = true;
            }
            else
            {
                IsHost = false;
            }

            CurrentLobby = lobby;

            status?.Report(IsHost
                ? $"Lobby created. (Host)\nLobbyId={lobby.Id}"
                : $"Lobby joined. (Client)\nLobbyId={lobby.Id}");

            return true;
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("[QuickSessionContext] QuickSession fallback: canceled/timeout.");
            status?.Report("Canceled / Timeout");
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[QuickSessionContext] QuickSession fallback: failed.\n{ex}");
            status?.Report("Failed. Check console.");
            return false;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task EnsureSignedInAsync(CancellationToken externalCt)
    {
        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _servicesInitTimeoutSeconds);

        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();

        timeoutCts.Token.ThrowIfCancellationRequested();

        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

        timeoutCts.Token.ThrowIfCancellationRequested();
    }

    private string GetOrGeneratePlayerName(string inputName)
    {
        string name = string.IsNullOrWhiteSpace(inputName) ? null : inputName.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            name = GenerateName();
            Debug.LogWarning($"[QuickSessionContext] Username fallback 발생: empty input. Generated={name}");
        }

        PlayerPrefs.SetString(PrefKeyPlayerName, name);
        PlayerPrefs.Save();
        return name;
    }

    private static string GenerateName()
    {
        int n = UnityEngine.Random.Range(0, 10000);
        return $"Player_{n:0000}";
    }

    private static Player BuildPlayer(string playerName)
    {
        var data = new Dictionary<string, PlayerDataObject>
        {
            { "name", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, playerName) }
        };

        return new Player(id: AuthenticationService.Instance.PlayerId, data: data);
    }

    private async Task<Lobby> TryQuickJoinAsync(string playerName, CancellationToken externalCt)
    {
        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _quickJoinTimeoutSeconds);

        try
        {
            var options = new QuickJoinLobbyOptions
            {
                Player = BuildPlayer(playerName),
                Filter = new List<QueryFilter>
                {
                    new QueryFilter(
                        field: QueryFilter.FieldOptions.MaxPlayers,
                        op: QueryFilter.OpOptions.EQ,
                        value: _maxPlayers.ToString()
                    )
                }
            };

            Task<Lobby> req = LobbyService.Instance.QuickJoinLobbyAsync(options);
            return await WaitWithCancellation(req, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("[QuickSessionContext] QuickJoin fallback 발생: timeout/canceled.");
            return null;
        }
        catch (LobbyServiceException e)
        {
            Debug.LogWarning($"[QuickSessionContext] QuickJoin fallback 발생: {e.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[QuickSessionContext] QuickJoin fallback 발생: unexpected.\n{ex}");
            return null;
        }
    }

    private async Task<Lobby> CreateLobbyAsync(string playerName, CancellationToken externalCt)
    {
        using var timeoutCts = CreateLinkedTimeoutCts(externalCt, _createLobbyTimeoutSeconds);

        try
        {
            string lobbyName = $"QS_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var options = new CreateLobbyOptions
            {
                IsPrivate = _isPrivate,
                Player = BuildPlayer(playerName)
            };

            Task<Lobby> req = LobbyService.Instance.CreateLobbyAsync(lobbyName, _maxPlayers, options);
            return await WaitWithCancellation(req, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("[QuickSessionContext] CreateLobby fallback 발생: timeout/canceled.");
            throw;
        }
        catch (LobbyServiceException e)
        {
            Debug.LogWarning($"[QuickSessionContext] CreateLobby fallback 발생: {e.Message}");
            throw;
        }
    }

    private static CancellationTokenSource CreateLinkedTimeoutCts(CancellationToken externalCt, float timeoutSeconds)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        if (timeoutSeconds > 0f)
        {
            int ms = Mathf.Clamp((int)(timeoutSeconds * 1000f), 1, int.MaxValue);
            linked.CancelAfter(ms);
        }
        else
        {
            Debug.LogWarning("[QuickSessionContext] Timeout fallback 발생: timeoutSeconds <= 0. No timeout will be applied.");
        }

        return linked;
    }

    private static async Task<T> WaitWithCancellation<T>(Task<T> task, CancellationToken ct)
    {
        Task cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        Task finished = await Task.WhenAny(task, cancelTask);
        if (finished == cancelTask)
            throw new OperationCanceledException(ct);

        return await task;
    }
}
