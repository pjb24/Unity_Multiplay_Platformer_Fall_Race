using UnityEngine;

/// <summary>
/// 추상 로더 키를 기반으로 캐릭터 비주얼 프리팹을 로딩/생성합니다.
/// </summary>
public static class CharacterVisualFactory
{
    /// <summary>
    /// 로더 키로 캐릭터 프리팹을 로딩합니다. 실패 시 null을 반환합니다.
    /// </summary>
    public static GameObject TryLoadCharacterPrefab(string loaderKey)
    {
        if (string.IsNullOrEmpty(loaderKey))
            return null;

        // 기본 전략: Resources 경로를 loaderKey로 간주.
        // 프로젝트 정책에 맞춰 Addressables/RemoteBundle 로더로 교체 가능.
        GameObject prefab = Resources.Load<GameObject>(loaderKey);
        return prefab;
    }

    /// <summary>
    /// 안전한 fallback 캡슐 비주얼을 동적으로 생성합니다.
    /// </summary>
    public static GameObject CreateFallbackCapsule(string name)
    {
        GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        capsule.name = string.IsNullOrEmpty(name) ? "FallbackCapsule" : name;

        // 물리 간섭을 피하기 위해 Collider를 제거합니다.
        Collider capsuleCollider = capsule.GetComponent<Collider>();
        if (capsuleCollider != null)
            Object.Destroy(capsuleCollider);

        return capsule;
    }
}
