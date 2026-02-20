using System.Text;
using UnityEngine;

/// <summary>
/// 로비/게임 전 구간에서 공통으로 사용하는 표시 이름 규칙을 제공합니다.
/// </summary>
public static class DisplayNamePolicy
{
    /// <summary>
    /// 표시 이름 최대 길이 제한 값입니다.
    /// </summary>
    public const int MaxDisplayNameLength = 24;

    /// <summary>
    /// 비어 있는 이름 입력 시 생성할 기본 접두사입니다.
    /// </summary>
    private const string DefaultPrefix = "User";

    /// <summary>
    /// 원본 입력 이름을 규칙에 맞게 정규화합니다.
    /// </summary>
    public static string Sanitize(string rawName)
    {
        // null/공백 입력을 빈 문자열로 통일한 중간 변수입니다.
        string trimmed = string.IsNullOrWhiteSpace(rawName) ? string.Empty : rawName.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return BuildRandomDefaultName();

        if (trimmed.Length > MaxDisplayNameLength)
            return trimmed.Substring(0, MaxDisplayNameLength);

        return trimmed;
    }

    /// <summary>
    /// NGO 연결 페이로드용 UTF-8 바이트 배열을 생성합니다.
    /// </summary>
    public static byte[] BuildConnectionPayload(string rawName)
    {
        // 페이로드에 싣기 전에 정규화한 이름 문자열입니다.
        string sanitizedName = Sanitize(rawName);
        return Encoding.UTF8.GetBytes(sanitizedName);
    }

    /// <summary>
    /// NGO 연결 페이로드에서 표시 이름을 복원합니다.
    /// </summary>
    public static string ParseConnectionPayload(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
            return BuildRandomDefaultName();

        // UTF-8 디코딩 결과 원본 문자열입니다.
        string decoded = Encoding.UTF8.GetString(payload);
        return Sanitize(decoded);
    }

    /// <summary>
    /// 무작위 기본 사용자 이름을 생성합니다.
    /// </summary>
    private static string BuildRandomDefaultName()
    {
        // 랜덤 기본 이름으로 사용할 4자리 난수 값입니다.
        int randomNumber = Random.Range(1000, 9999);
        return $"{DefaultPrefix}{randomNumber}";
    }
}
