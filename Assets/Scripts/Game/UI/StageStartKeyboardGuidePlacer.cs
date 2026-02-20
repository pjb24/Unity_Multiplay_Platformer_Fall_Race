using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Stage 1~3 시작 지점에 영어 키보드 조작 가이드를 월드 공간 텍스트로 배치한다.
/// </summary>
public sealed class StageStartKeyboardGuidePlacer : MonoBehaviour
{
    [Serializable]
    private sealed class StageStartGuideAnchor
    {
        // 검수 시 스테이지를 식별하기 위한 이름 라벨이다.
        [Tooltip("검수용 스테이지 이름 라벨")]
        [SerializeField] private string _stageLabel;

        // 가이드를 띄울 기준이 되는 해당 스테이지 시작 지점 Transform이다.
        [Tooltip("해당 스테이지의 시작 지점 Transform")]
        [SerializeField] private Transform _startPoint;

        /// <summary>
        /// 검수용 스테이지 이름 라벨을 반환한다.
        /// </summary>
        public string StageLabel => _stageLabel;

        /// <summary>
        /// 해당 스테이지의 시작 지점 Transform을 반환한다.
        /// </summary>
        public Transform StartPoint => _startPoint;
    }

    [Header("Stage 1~3 Start Points")]
    // Stage 1~3 시작 위치 참조만 보관하는 앵커 목록이다.
    [Tooltip("Stage 1~3 시작 위치만 순서대로 연결한다.")]
    [SerializeField] private List<StageStartGuideAnchor> _stageAnchors = new List<StageStartGuideAnchor>(3);

    [Header("Floating Placement")]
    // 시작 지점 기준으로 텍스트를 전방으로 이동시키는 거리 값이다.
    [Tooltip("시작 지점 기준 전방 거리")]
    [SerializeField] private float _forwardOffset = 2.6f;

    // 시작 지점 기준으로 텍스트를 위로 올리는 높이 값이다.
    [Tooltip("시작 지점 기준 높이")]
    [SerializeField] private float _heightOffset = 1.45f;

    // 텍스트를 살짝 아래로 기울여 자연스럽게 읽히도록 하는 각도 값이다.
    [Tooltip("필요 시 아래 방향으로 살짝 기울일 각도")]
    [SerializeField] private float _downwardTilt = 8f;

    [Header("Guide Text (Fixed Spec)")]
    // 이동 안내 첫 줄 고정 문구이다.
    [Tooltip("첫 번째 줄 고정 문구")]
    [SerializeField] private string _moveLine = "MOVE: WASD / ARROW KEYS";

    // 점프 안내 둘째 줄 고정 문구이다.
    [Tooltip("두 번째 줄 고정 문구")]
    [SerializeField] private string _jumpLine = "JUMP: SPACEBAR";

    [Header("Text Style")]
    // Stage 1~3에서 공통으로 사용할 폰트 에셋 참조이다.
    [Tooltip("Stage 1~3 공통 폰트 에셋")]
    [SerializeField] private TMP_FontAsset _fontAsset;

    // Stage 1~3에서 동일하게 적용할 글자 크기 값이다.
    [Tooltip("Stage 1~3 공통 글자 크기")]
    [SerializeField] private float _fontSize = 3.6f;

    // Stage 1~3에서 동일하게 적용할 줄 간격 값이다.
    [Tooltip("Stage 1~3 공통 줄 간격")]
    [SerializeField] private float _lineSpacing = 12f;

    // 안내 문구 본문 색상 값이다.
    [Tooltip("텍스트 색상")]
    [SerializeField] private Color _textColor = Color.white;

    // 안내 문구 정렬 기준 값이다.
    [Tooltip("텍스트 정렬")]
    [SerializeField] private TextAlignmentOptions _alignment = TextAlignmentOptions.Center;

    [Header("Visibility Options")]
    // 텍스트 외곽선을 사용해 배경 대비를 높일지 여부이다.
    [Tooltip("밝거나 어두운 배경에서 대비를 높이기 위한 외곽선 사용 여부")]
    [SerializeField] private bool _useOutline = true;

    // 외곽선 색상 값이다.
    [Tooltip("외곽선 색상")]
    [SerializeField] private Color _outlineColor = Color.black;

    // 외곽선 두께 값이다.
    [Tooltip("외곽선 두께")]
    [SerializeField] private float _outlineWidth = 0.2f;

    // 시인성 보강용 배경판을 표시할지 여부이다.
    [Tooltip("시인성 보조용 배경판 사용 여부")]
    [SerializeField] private bool _useBackgroundPanel;

    // 배경판 가로/세로 크기 값이다.
    [Tooltip("배경판 가로/세로 크기")]
    [SerializeField] private Vector2 _backgroundSize = new Vector2(6.8f, 2.7f);

    // 텍스트와 배경판 사이 깊이 간격 값이다.
    [Tooltip("배경판과 텍스트 사이 거리")]
    [SerializeField] private float _backgroundDepth = 0.03f;

    // 생성된 가이드들을 정리하기 위한 부모 루트 오브젝트 이름이다.
    private const string GuideRootName = "StageStartKeyboardGuideRoot";

    /// <summary>
    /// 시작 시 Stage 1~3 가이드를 생성한다.
    /// </summary>
    private void Start()
    {
        // RebuildGuides();
    }

#if UNITY_EDITOR
    /// <summary>
    /// Inspector 값 검증 시 범위 보정을 수행한다.
    /// </summary>
    private void OnValidate()
    {
        _fontSize = Mathf.Max(0.1f, _fontSize);
        _lineSpacing = Mathf.Max(-200f, _lineSpacing);
        _outlineWidth = Mathf.Clamp01(_outlineWidth);
        _backgroundDepth = Mathf.Max(0f, _backgroundDepth);
        _backgroundSize.x = Mathf.Max(0.1f, _backgroundSize.x);
        _backgroundSize.y = Mathf.Max(0.1f, _backgroundSize.y);
    }
#endif

    /// <summary>
    /// 기존 가이드를 정리하고 Stage 1~3 시작 지점에 다시 생성한다.
    /// </summary>
    [ContextMenu("Rebuild Stage Start Keyboard Guides")]
    public void RebuildGuides()
    {
        Transform root = GetOrCreateGuideRoot();
        ClearChildren(root);

        for (int i = 0; i < _stageAnchors.Count && i < 3; i++)
        {
            StageStartGuideAnchor anchor = _stageAnchors[i];
            if (anchor == null || anchor.StartPoint == null)
            {
                continue;
            }

            CreateGuideForAnchor(root, anchor, i + 1);
        }
    }

    /// <summary>
    /// 가이드들을 담을 루트 오브젝트를 반환하거나 생성한다.
    /// </summary>
    private Transform GetOrCreateGuideRoot()
    {
        Transform existing = transform.Find(GuideRootName);
        if (existing != null)
        {
            return existing;
        }

        var root = new GameObject(GuideRootName);
        root.transform.SetParent(transform, false);
        return root.transform;
    }

    /// <summary>
    /// 루트 하위의 기존 가이드 오브젝트를 모두 제거한다.
    /// </summary>
    private static void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
        {
            Transform child = root.GetChild(i);
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                DestroyImmediate(child.gameObject);
                continue;
            }
#endif
            Destroy(child.gameObject);
        }
    }

    /// <summary>
    /// 단일 스테이지 시작 지점용 가이드를 생성한다.
    /// </summary>
    private void CreateGuideForAnchor(Transform root, StageStartGuideAnchor anchor, int stageNumber)
    {
        Transform startPoint = anchor.StartPoint;
        Vector3 position = startPoint.position + (startPoint.forward * _forwardOffset) + (Vector3.up * _heightOffset);

        Quaternion rotation = Quaternion.LookRotation(startPoint.forward, Vector3.up) * Quaternion.Euler(_downwardTilt, 0f, 0f);

        string stageLabel = string.IsNullOrWhiteSpace(anchor.StageLabel) ? $"Stage {stageNumber}" : anchor.StageLabel;
        var guideObject = new GameObject($"{stageLabel}_KeyboardGuide");
        guideObject.transform.SetParent(root, false);
        guideObject.transform.SetPositionAndRotation(position, rotation);

        TMP_Text text = guideObject.AddComponent<TextMeshPro>();
        ConfigureTextStyle(text);
        text.text = $"{_moveLine}\n{_jumpLine}";

        if (_useBackgroundPanel)
        {
            CreateBackgroundPanel(guideObject.transform);
        }
    }

    /// <summary>
    /// 모든 스테이지에서 동일하게 사용할 텍스트 스타일을 적용한다.
    /// </summary>
    private void ConfigureTextStyle(TMP_Text text)
    {
        text.font = _fontAsset;
        text.fontSize = _fontSize;
        text.lineSpacing = _lineSpacing;
        text.color = _textColor;
        text.alignment = _alignment;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.richText = false;
        text.horizontalAlignment = HorizontalAlignmentOptions.Center;
        text.verticalAlignment = VerticalAlignmentOptions.Middle;

        if (_useOutline)
        {
            text.fontSharedMaterial = new Material(text.fontSharedMaterial);
            text.fontSharedMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, _outlineWidth);
            text.fontSharedMaterial.SetColor(ShaderUtilities.ID_OutlineColor, _outlineColor);
        }
    }

    /// <summary>
    /// 필요 시 시인성 향상을 위한 배경판을 생성한다.
    /// </summary>
    private void CreateBackgroundPanel(Transform parent)
    {
        // 시인성 보조용 배경판 오브젝트 참조이다.
        var panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
        panel.name = "GuideBackgroundPanel";
        panel.transform.SetParent(parent, false);
        panel.transform.localPosition = new Vector3(0f, 0f, _backgroundDepth);
        panel.transform.localScale = new Vector3(_backgroundSize.x, _backgroundSize.y, 1f);

        // 생성 직후 물리 충돌을 즉시 비활성화하기 위한 배경판 콜라이더 참조이다.
        var collider = panel.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }
    }
}
