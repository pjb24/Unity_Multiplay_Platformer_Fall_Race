using UnityEngine;
using Unity.Netcode;

public sealed class Bootstrapper : MonoBehaviour
{
    private static Bootstrapper _instance;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("[Bootstrapper] Duplicate Bootstrapper detected. Destroying.");
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[Bootstrapper] NetworkManager.Singleton is null. Check NetworkRoot setup.");
        }
    }
}
