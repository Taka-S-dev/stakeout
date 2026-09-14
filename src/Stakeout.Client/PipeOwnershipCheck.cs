using System.Security.AccessControl;
using System.Security.Principal;

namespace Stakeout.Client;

/// <summary>
/// CurrentUserOnly 無しで繋いだパイプが、自分のデーモンのものかを確かめる（ADR 0024）。
///
/// 管理者の CLI が本人 SID 所有のパイプに繋ぐときは、.NET の CurrentUserOnly の検査
/// （所有者 == 自分の Owner = Administrators）が通らない。そのときだけ検査を自前で行う。
/// 条件は .NET の検査より緩めない: 所有者は本人か Administrators、許可は本人・Administrators・SYSTEM だけ。
///
/// COM にも OS にも触れないので単体テストできる。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class PipeOwnershipCheck
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <returns>拒否する理由。受け入れるなら null。</returns>
    public static string? Reject(SecurityIdentifier me, SecurityIdentifier? owner, IEnumerable<(SecurityIdentifier Identity, AccessControlType Type)> rules)
    {
        if (owner is null)
        {
            return "パイプの所有者が読めません。";
        }

        if (owner != me && owner != Administrators)
        {
            return $"パイプの所有者が自分でも Administrators でもありません ({owner})。";
        }

        foreach (var (identity, type) in rules)
        {
            if (type == AccessControlType.Allow && identity != me && identity != Administrators && identity != LocalSystem)
            {
                return $"パイプが自分以外にも開かれています ({identity})。";
            }
        }

        return null;
    }
}
