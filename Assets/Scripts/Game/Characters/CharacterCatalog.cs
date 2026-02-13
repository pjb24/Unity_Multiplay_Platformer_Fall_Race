using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 캐릭터 원본 에셋을 직접 저장소에 보관하지 않고,
/// 추상 참조 키 기반으로 런타임 로딩하기 위한 카탈로그입니다.
/// </summary>
[CreateAssetMenu(menuName = "Game/Character Catalog", fileName = "CharacterCatalog")]
public sealed class CharacterCatalog : ScriptableObject
{
    [Serializable]
    public struct CharacterDefinition
    {
        /// <summary>캐릭터 고유 ID(네트워크 동기화 키)입니다.</summary>
        public string characterId;

        /// <summary>Addressables/RemoteBundle/Resources 등 로더가 해석할 추상 키입니다.</summary>
        public string loaderKey;

        /// <summary>카탈로그 엔트리 버전입니다.</summary>
        public string version;

        /// <summary>원본 무결성 추적용 해시 문자열입니다.</summary>
        public string hash;

        /// <summary>캐릭터별 전용 Animator Controller(옵션)입니다.</summary>
        public RuntimeAnimatorController animatorController;
    }

    [Header("Catalog Entries")]
    [SerializeField] private List<CharacterDefinition> _characters = new List<CharacterDefinition>(6);

    [Header("Fallback")]
    [SerializeField] private string _fallbackCharacterId = "capsule_fallback";
    [SerializeField] private string _fallbackLoaderKey = "fallback/capsule";

    /// <summary>등록 캐릭터 정의 목록입니다.</summary>
    public IReadOnlyList<CharacterDefinition> Characters => _characters;

    /// <summary>fallback 캐릭터 ID입니다.</summary>
    public string FallbackCharacterId => _fallbackCharacterId;

    /// <summary>fallback 로더 키입니다.</summary>
    public string FallbackLoaderKey => _fallbackLoaderKey;
}
