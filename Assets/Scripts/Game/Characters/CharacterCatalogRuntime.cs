using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CharacterCatalog를 런타임 조회 가능한 형태로 캐싱하는 유틸리티입니다.
/// </summary>
public static class CharacterCatalogRuntime
{
    /// <summary>Resources에서 카탈로그를 로드할 기본 경로입니다.</summary>
    private const string CatalogResourcePath = "CharacterCatalog";

    /// <summary>로드된 카탈로그 참조를 캐싱하는 변수입니다.</summary>
    private static CharacterCatalog _cachedCatalog;

    /// <summary>카탈로그 인덱스(캐릭터ID -> 정의)를 캐싱하는 맵입니다.</summary>
    private static Dictionary<string, CharacterCatalog.CharacterDefinition> _definitionById;

    /// <summary>
    /// 카탈로그를 반환합니다. 없으면 Resources 경로에서 시도합니다.
    /// </summary>
    public static CharacterCatalog GetCatalog()
    {
        if (_cachedCatalog != null)
            return _cachedCatalog;

        _cachedCatalog = Resources.Load<CharacterCatalog>(CatalogResourcePath);
        if (_cachedCatalog == null)
        {
            Debug.LogWarning("[CharacterCatalogRuntime] Fallback 발생: Resources/CharacterCatalog.asset missing.");
            return null;
        }

        BuildIndex();
        return _cachedCatalog;
    }

    /// <summary>
    /// 캐릭터 ID로 정의를 조회합니다.
    /// </summary>
    public static bool TryGetDefinition(string characterId, out CharacterCatalog.CharacterDefinition definition)
    {
        definition = default;

        CharacterCatalog catalog = GetCatalog();
        if (catalog == null)
            return false;

        if (_definitionById == null)
            BuildIndex();

        if (string.IsNullOrEmpty(characterId))
            return false;

        return _definitionById.TryGetValue(characterId, out definition);
    }

    /// <summary>
    /// 현재 카탈로그에 등록된 캐릭터 ID 목록을 반환합니다.
    /// </summary>
    public static List<string> BuildRegisteredCharacterIds()
    {
        var result = new List<string>(8);

        CharacterCatalog catalog = GetCatalog();
        if (catalog == null)
            return result;

        IReadOnlyList<CharacterCatalog.CharacterDefinition> characters = catalog.Characters;
        for (int i = 0; i < characters.Count; i++)
        {
            CharacterCatalog.CharacterDefinition definition = characters[i];
            if (string.IsNullOrEmpty(definition.characterId))
                continue;

            result.Add(definition.characterId);
        }

        return result;
    }

    /// <summary>
    /// 카탈로그 정의 인덱스를 생성합니다.
    /// </summary>
    private static void BuildIndex()
    {
        _definitionById = new Dictionary<string, CharacterCatalog.CharacterDefinition>(16);

        if (_cachedCatalog == null)
            return;

        IReadOnlyList<CharacterCatalog.CharacterDefinition> characters = _cachedCatalog.Characters;
        for (int i = 0; i < characters.Count; i++)
        {
            CharacterCatalog.CharacterDefinition definition = characters[i];
            if (string.IsNullOrEmpty(definition.characterId))
                continue;

            _definitionById[definition.characterId] = definition;
        }
    }
}
