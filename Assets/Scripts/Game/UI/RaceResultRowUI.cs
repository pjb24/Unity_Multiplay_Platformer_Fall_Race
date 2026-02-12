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
    /// 한 행의 결과 텍스트를 바인딩합니다.
    /// </summary>
    public void Bind(string playerName, string stage1, string stage2, string stage3, string total)
    {
        SetTextSafe(_txtPlayerName, playerName);
        SetTextSafe(_txtStage1, stage1);
        SetTextSafe(_txtStage2, stage2);
        SetTextSafe(_txtStage3, stage3);
        SetTextSafe(_txtTotal, total);
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
