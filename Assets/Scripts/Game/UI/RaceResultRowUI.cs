using TMPro;
using UnityEngine;

public sealed class RaceResultRowUI : MonoBehaviour
{
    /// <summary>
    /// 플레이어 이름 셀 텍스트입니다.
    /// </summary>
    [SerializeField] private TMP_Text _txtPlayerName;

    /// <summary>
    /// Stage 1 기록 셀 텍스트입니다.
    /// </summary>
    [SerializeField] private TMP_Text _txtStage1;

    /// <summary>
    /// Stage 2 기록 셀 텍스트입니다.
    /// </summary>
    [SerializeField] private TMP_Text _txtStage2;

    /// <summary>
    /// Stage 3 기록 셀 텍스트입니다.
    /// </summary>
    [SerializeField] private TMP_Text _txtStage3;

    /// <summary>
    /// Total 기록 셀 텍스트입니다.
    /// </summary>
    [SerializeField] private TMP_Text _txtTotal;

    /// <summary>
    /// 인스펙터 참조가 비어있는 경우 런타임에 텍스트 셀 참조를 보정합니다.
    /// </summary>
    private void Awake()
    {
        EnsureCellTextReferences();
    }

    /// <summary>
    /// 한 행의 결과 텍스트를 바인딩합니다.
    /// </summary>
    public void Bind(string playerName, string stage1, string stage2, string stage3, string total)
    {
        EnsureCellTextReferences();

        // 공통 정책으로 정규화한 결과표 사용자 이름 문자열입니다.
        string lobbyUserName = DisplayNamePolicy.Sanitize(playerName);

        SetTextSafe(_txtPlayerName, lobbyUserName);
        SetTextSafe(_txtStage1, stage1);
        SetTextSafe(_txtStage2, stage2);
        SetTextSafe(_txtStage3, stage3);
        SetTextSafe(_txtTotal, total);
    }

    /// <summary>
    /// 텍스트 셀 참조가 누락된 경우 자식 TMP 텍스트에서 자동으로 연결합니다.
    /// </summary>
    private void EnsureCellTextReferences()
    {
        if (_txtPlayerName != null && _txtStage1 != null && _txtStage2 != null && _txtStage3 != null && _txtTotal != null)
            return;

        // 현재 행 오브젝트 하위에서 검색한 TMP 텍스트 배열입니다.
        TMP_Text[] texts = GetComponentsInChildren<TMP_Text>(true);
        if (texts == null || texts.Length == 0)
            return;

        if (_txtPlayerName == null)
            _txtPlayerName = FindTextByKeywords(texts, "player", "name") ?? FindTextByKeywords(texts, "player") ?? texts[0];
        if (_txtStage1 == null)
            _txtStage1 = FindTextByKeywords(texts, "stage", "1") ?? FindTextByKeywords(texts, "s1");
        if (_txtStage2 == null)
            _txtStage2 = FindTextByKeywords(texts, "stage", "2") ?? FindTextByKeywords(texts, "s2");
        if (_txtStage3 == null)
            _txtStage3 = FindTextByKeywords(texts, "stage", "3") ?? FindTextByKeywords(texts, "s3");
        if (_txtTotal == null)
            _txtTotal = FindTextByKeywords(texts, "total") ?? FindTextByKeywords(texts, "sum");
    }

    /// <summary>
    /// 텍스트 오브젝트 이름에 키워드가 모두 포함된 TMP 텍스트를 찾습니다.
    /// </summary>
    private static TMP_Text FindTextByKeywords(TMP_Text[] texts, params string[] keywords)
    {
        for (int i = 0; i < texts.Length; i++)
        {
            // 비교 대상 TMP 오브젝트 이름의 소문자 문자열입니다.
            string lowerName = texts[i].name.ToLowerInvariant();
            bool containsAllKeywords = true;

            for (int k = 0; k < keywords.Length; k++)
            {
                if (!lowerName.Contains(keywords[k]))
                {
                    containsAllKeywords = false;
                    break;
                }
            }

            if (containsAllKeywords)
                return texts[i];
        }

        return null;
    }

    /// <summary>
    /// null 안전하게 TMP 텍스트를 갱신합니다.
    /// </summary>
    private void SetTextSafe(TMP_Text target, string value)
    {
        if (target != null)
            target.text = value;
    }
}
