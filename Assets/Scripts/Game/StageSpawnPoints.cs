using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stage별 SpawnPoint 묶음.
/// - stage index 기반으로 스폰 포인트를 반환한다.
/// - 유효하지 않은 요청은 false 반환 (호출자가 fallback 처리).
/// </summary>
public sealed class StageSpawnPoints : MonoBehaviour
{
    [Serializable]
    public sealed class StageSpawnSet
    {
        public string stageName;
        public Transform parent;
    }

    [SerializeField] private List<StageSpawnSet> _stages = new List<StageSpawnSet>();
    [SerializeField] private Transform _fallbackSpawn;

    public Transform FallbackSpawn => _fallbackSpawn;

    public bool TryGetSpawnPoint(int stageIndex, int slotIndex, out Transform spawn)
    {
        spawn = null;

        if (stageIndex < 0 || stageIndex >= _stages.Count)
            return false;

        var stage = _stages[stageIndex];
        if (stage == null || stage.parent == null)
            return false;

        int childCount = stage.parent.childCount;
        if (childCount <= 0)
            return false;

        int clamped = Mathf.Clamp(slotIndex, 0, childCount - 1);
        spawn = stage.parent.GetChild(clamped);
        return spawn != null;
    }
}
