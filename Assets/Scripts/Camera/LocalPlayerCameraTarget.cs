using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 네트워크 플레이어의 카메라 타깃 등록과 머리 위 이름표 표시를 함께 처리하는 컴포넌트입니다.
/// </summary>
public sealed class LocalPlayerCameraTarget : NetworkBehaviour
{
    [Header("Camera Target")]
    [SerializeField] private Transform _cameraTarget; // 로컬 플레이어 카메라가 따라갈 기준 트랜스폼입니다.

    [Header("NameTag")]
    [SerializeField] private Transform _nameTagAnchor; // 이름표가 추적할 기준 트랜스폼(직접 지정 시 최우선)입니다.

    [SerializeField] private bool _autoFindHeadBoneAnchor = true; // NameTag Anchor가 비어 있으면 Head 본을 자동 탐색할지 여부입니다.

    [SerializeField] private Renderer _nameTagHeightRenderer; // Head 본이 없을 때 캐릭터 상단 높이를 계산할 렌더러 참조입니다.

    [SerializeField] private Vector3 _nameTagOffset = new Vector3(0f, 0.3f, 0f); // 기준 위치(Head 또는 Bounds Top)에서 추가로 올릴 오프셋 값입니다.

    [SerializeField] private float _fallbackHeightFromRoot = 2.2f; // 앵커/렌더러가 모두 없을 때 루트에서 올릴 기본 높이입니다.

    [SerializeField] private float _nameTagVisibleDistance = 35f; // 이름표가 표시되는 최대 거리입니다.

    [SerializeField] private bool _hideLocalPlayerNameTag = true; // 로컬 플레이어 본인의 이름표를 숨길지 여부입니다.

    [SerializeField] private bool _hideWhenOffScreen = true; // 화면 밖으로 나간 이름표를 숨길지 여부입니다.

    [SerializeField] private bool _useLineOfSightCheck = false; // 카메라와 이름표 사이 장애물 유무를 검사할지 여부입니다.

    [SerializeField] private LayerMask _lineOfSightMask = Physics.DefaultRaycastLayers; // Line Of Sight 검사에 사용할 물리 레이어 마스크입니다.

    [SerializeField] private float _fontSize = 3f;

    [SerializeField] private int _maxDisplayNameLength = 24; // 이름표에 노출할 최대 글자 수입니다.

    [SerializeField] private Color _friendlyNameColor = new Color(1f, 1f, 1f, 1f); // 기본 이름표 텍스트 색상입니다.

    [SerializeField] private Color _enemyNameColor = new Color(1f, 0.45f, 0.45f, 1f); // 팀 시스템 연동 시 적군 이름표에 사용할 예약 색상입니다.

    [SerializeField] private Color _outlineColor = new Color(0f, 0f, 0f, 1f); // 이름표 가독성 향상을 위한 외곽선 색상입니다.

    [SerializeField] private float _outlineWidth = 0.2f; // 이름표 외곽선 두께 값입니다.

    [SerializeField] private float _refreshIntervalSec = 0.1f; // 거리/가시성 계산을 수행할 간격(초)입니다.

    private readonly NetworkVariable<FixedString64Bytes> _displayName = new NetworkVariable<FixedString64Bytes>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server); // 플레이어 표시 이름을 클라이언트 전체에 동기화하는 네트워크 변수입니다.

    private const string K_NameTagObjectName = "PlayerNameTag_Text"; // 이름표 텍스트 런타임 오브젝트의 고정 이름입니다.

    private Camera _mainCamera; // 현재 씬의 메인 카메라 캐시입니다.

    private Transform _resolvedAnchor; // 이름표 위치 계산에 실제로 사용할 기준 트랜스폼입니다.

    private Transform _nameTagTransform; // 런타임 이름표 텍스트 오브젝트의 트랜스폼입니다.

    private TextMeshPro _nameTagText; // 런타임 이름표 텍스트 렌더링 컴포넌트입니다.

    private float _nextRefreshTime; // 다음 가시성 계산을 수행할 예정 시각입니다.

    /// <summary>
    /// 네트워크 스폰 시 카메라 타깃 등록, 이름표 동기화 요청, UI 생성을 수행합니다.
    /// </summary>
    public override void OnNetworkSpawn()
    {
        _resolvedAnchor = ResolveNameTagAnchor();
        EnsureNameTagView();
        RefreshNameTagText(_displayName.Value);

        _displayName.OnValueChanged += OnDisplayNameChanged;

        if (IsOwner)
        {
            Transform target = _cameraTarget != null ? _cameraTarget : transform;
            LocalPlayerTargetBus.Publish(target);
            SubmitDisplayNameServerRpc(new FixedString64Bytes(BuildInitialDisplayName()));
        }
    }

    /// <summary>
    /// 네트워크 디스폰 시 이벤트와 로컬 이름표 오브젝트를 정리합니다.
    /// </summary>
    public override void OnNetworkDespawn()
    {
        _displayName.OnValueChanged -= OnDisplayNameChanged;

        if (IsOwner)
        {
            Transform target = _cameraTarget != null ? _cameraTarget : transform;
            LocalPlayerTargetBus.Clear(target);
        }

        if (_nameTagTransform != null)
            Destroy(_nameTagTransform.gameObject);

        _nameTagTransform = null;
        _nameTagText = null;
    }

    /// <summary>
    /// 이름표 위치/가시성/빌보드 계산을 프레임 말에 처리해 카메라 갱신 이후 값을 사용합니다.
    /// </summary>
    private void LateUpdate()
    {
        if (!IsSpawned)
            return;

        EnsureNameTagView();
        UpdateNameTagTransform();
        UpdateNameTagBillboard();

        if (Time.time >= _nextRefreshTime)
        {
            _nextRefreshTime = Time.time + Mathf.Max(0.05f, _refreshIntervalSec);
            UpdateNameTagVisibility();
        }
    }

    /// <summary>
    /// 오너 클라이언트가 자신의 표시 이름을 서버에 제출합니다.
    /// </summary>
    [Rpc(SendTo.Server)]
    private void SubmitDisplayNameServerRpc(FixedString64Bytes requestedName)
    {
        string sanitizedName = SanitizeDisplayName(requestedName.ToString(), OwnerClientId);
        _displayName.Value = new FixedString64Bytes(sanitizedName);
    }

    /// <summary>
    /// 네트워크 표시 이름 값이 변경되면 텍스트를 즉시 갱신합니다.
    /// </summary>
    private void OnDisplayNameChanged(FixedString64Bytes previous, FixedString64Bytes current)
    {
        RefreshNameTagText(current);
    }

    /// <summary>
    /// 이름표 표시 텍스트를 현재 네트워크 값으로 갱신합니다.
    /// </summary>
    private void RefreshNameTagText(FixedString64Bytes displayName)
    {
        if (_nameTagText == null)
            return;

        string sanitizedName = SanitizeDisplayName(displayName.ToString(), OwnerClientId);
        _nameTagText.text = sanitizedName;
        _nameTagText.color = _friendlyNameColor;
    }

    /// <summary>
    /// 이름표 텍스트 오브젝트를 재사용/생성하고 중복 인스턴스를 제거합니다.
    /// </summary>
    private void EnsureNameTagView()
    {
        if (_nameTagTransform != null && _nameTagText != null)
            return;

        CleanupDuplicateNameTags();

        Transform existing = transform.Find(K_NameTagObjectName);
        if (existing != null)
        {
            _nameTagTransform = existing;
            _nameTagText = existing.GetComponent<TextMeshPro>();
        }

        if (_nameTagTransform == null)
        {
            GameObject textObject = new GameObject(K_NameTagObjectName);
            _nameTagTransform = textObject.transform;
            _nameTagTransform.SetParent(transform, false);
        }

        if (_nameTagText == null)
            _nameTagText = _nameTagTransform.gameObject.GetComponent<TextMeshPro>() ?? _nameTagTransform.gameObject.AddComponent<TextMeshPro>();

        ApplyNameTagStyle();
    }

    /// <summary>
    /// 이름표 텍스트 스타일을 한 번에 적용합니다.
    /// </summary>
    private void ApplyNameTagStyle()
    {
        if (_nameTagText == null)
            return;

        _nameTagText.fontSize = _fontSize;
        _nameTagText.alignment = TextAlignmentOptions.Center;
        _nameTagText.textWrappingMode = TextWrappingModes.NoWrap;
        _nameTagText.overflowMode = TextOverflowModes.Ellipsis;
        _nameTagText.outlineColor = _outlineColor;
        _nameTagText.outlineWidth = _outlineWidth;
        _nameTagText.color = _friendlyNameColor;
    }

    /// <summary>
    /// 동일 이름의 중복 이름표 오브젝트를 제거해 무한 생성처럼 보이는 누적 문제를 차단합니다.
    /// </summary>
    private void CleanupDuplicateNameTags()
    {
        int foundCount = 0;
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (!string.Equals(child.name, K_NameTagObjectName, System.StringComparison.Ordinal))
                continue;

            foundCount++;
            if (foundCount == 1)
                continue;

            Destroy(child.gameObject);
        }
    }

    /// <summary>
    /// 이름표 위치를 기준 트랜스폼과 오프셋 값으로 갱신합니다.
    /// </summary>
    private void UpdateNameTagTransform()
    {
        if (_nameTagTransform == null)
            return;

        _resolvedAnchor = ResolveNameTagAnchor();
        if (_resolvedAnchor != null)
        {
            _nameTagTransform.position = _resolvedAnchor.position + _nameTagOffset;
            return;
        }

        if (_nameTagHeightRenderer != null)
        {
            Bounds bounds = _nameTagHeightRenderer.bounds;
            Vector3 topPosition = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
            _nameTagTransform.position = topPosition + _nameTagOffset;
            return;
        }

        Vector3 rootPosition = transform.position;
        _nameTagTransform.position = rootPosition + new Vector3(_nameTagOffset.x, _fallbackHeightFromRoot + _nameTagOffset.y, _nameTagOffset.z);
    }

    /// <summary>
    /// 이름표의 기준 위치를 Anchor > HeadBone > Renderer Bounds 순으로 결정합니다.
    /// </summary>
    private Transform ResolveNameTagAnchor()
    {
        if (_nameTagAnchor != null)
            return _nameTagAnchor;

        if (_autoFindHeadBoneAnchor)
        {
            Animator animator = GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null)
                    return head;
            }

            Transform headByName = transform.Find("Armature/Hips/Spine/Chest/Neck/Head") ?? transform.Find("Head");
            if (headByName != null)
                return headByName;
        }

        if (_nameTagHeightRenderer == null)
            _nameTagHeightRenderer = GetComponentInChildren<Renderer>();

        return null;
    }

    /// <summary>
    /// 이름표가 카메라를 바라보도록 회전 값을 갱신합니다.
    /// </summary>
    private void UpdateNameTagBillboard()
    {
        if (_nameTagTransform == null)
            return;

        _mainCamera = ResolveMainCamera(_mainCamera);
        if (_mainCamera == null)
            return;

        Vector3 forward = _nameTagTransform.position - _mainCamera.transform.position;
        if (forward.sqrMagnitude < 0.0001f)
            return;

        _nameTagTransform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    /// <summary>
    /// 거리/화면/Line Of Sight 정책을 적용하여 이름표 활성 상태를 결정합니다.
    /// </summary>
    private void UpdateNameTagVisibility()
    {
        if (_nameTagTransform == null)
            return;

        if (_hideLocalPlayerNameTag && IsOwner)
        {
            _nameTagTransform.gameObject.SetActive(false);
            return;
        }

        _mainCamera = ResolveMainCamera(_mainCamera);
        if (_mainCamera == null)
        {
            _nameTagTransform.gameObject.SetActive(true);
            return;
        }

        Vector3 cameraPosition = _mainCamera.transform.position;
        Vector3 nameTagPosition = _nameTagTransform.position;

        float sqrDistance = (cameraPosition - nameTagPosition).sqrMagnitude;
        if (sqrDistance > _nameTagVisibleDistance * _nameTagVisibleDistance)
        {
            _nameTagTransform.gameObject.SetActive(false);
            return;
        }

        if (_hideWhenOffScreen)
        {
            Vector3 viewport = _mainCamera.WorldToViewportPoint(nameTagPosition);
            bool isOffScreen = viewport.z < 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f;
            if (isOffScreen)
            {
                _nameTagTransform.gameObject.SetActive(false);
                return;
            }
        }

        if (_useLineOfSightCheck)
        {
            Vector3 direction = nameTagPosition - cameraPosition;
            if (Physics.Raycast(cameraPosition, direction.normalized, out RaycastHit hit, direction.magnitude, _lineOfSightMask, QueryTriggerInteraction.Ignore))
            {
                if (!hit.transform.IsChildOf(transform))
                {
                    _nameTagTransform.gameObject.SetActive(false);
                    return;
                }
            }
        }

        _nameTagTransform.gameObject.SetActive(true);
    }

    /// <summary>
    /// 표시 이름 정책(최대 길이, 공백 방지, 기본값)을 적용한 문자열을 반환합니다.
    /// </summary>
    private string SanitizeDisplayName(string rawName, ulong clientId)
    {
        string trimmed = string.IsNullOrWhiteSpace(rawName) ? string.Empty : rawName.Trim();
        if (string.IsNullOrEmpty(trimmed))
            trimmed = BuildDefaultDisplayName(clientId);

        if (trimmed.Length > Mathf.Max(1, _maxDisplayNameLength))
            trimmed = trimmed.Substring(0, Mathf.Max(1, _maxDisplayNameLength));

        return trimmed;
    }

    /// <summary>
    /// 로컬 사용자 이름 소스(로그인 닉네임)를 우선 사용해 초기 표시 이름을 구성합니다.
    /// </summary>
    private string BuildInitialDisplayName()
    {
        string preferredName = QuickSessionContext.Instance != null ? QuickSessionContext.Instance.LocalUsername : null;
        if (!string.IsNullOrWhiteSpace(preferredName))
            return preferredName;

        return BuildDefaultDisplayName(OwnerClientId);
    }

    /// <summary>
    /// 표시 이름이 없을 때 사용할 기본 플레이어 이름을 생성합니다.
    /// </summary>
    private static string BuildDefaultDisplayName(ulong clientId)
    {
        return $"Player {clientId}";
    }

    /// <summary>
    /// 메인 카메라 참조가 유효한지 확인하고 필요 시 Camera.main으로 재조회합니다.
    /// </summary>
    private static Camera ResolveMainCamera(Camera cachedCamera)
    {
        if (cachedCamera != null && cachedCamera.isActiveAndEnabled)
            return cachedCamera;

        return Camera.main;
    }
}
