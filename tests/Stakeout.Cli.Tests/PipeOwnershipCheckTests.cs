using System.Security.AccessControl;
using System.Security.Principal;
using Stakeout.Client;

namespace Stakeout.Cli.Tests;

/// <summary>
/// CurrentUserOnly 無しで繋いだパイプを、自分のデーモンと認めるかの判定（ADR 0024）。
/// .NET の CurrentUserOnly の検査より緩めない。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PipeOwnershipCheckTests
{
    private static readonly SecurityIdentifier Me = new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier Other = new("S-1-5-21-1-2-3-1002");
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    private static (SecurityIdentifier, AccessControlType) Allow(SecurityIdentifier sid) => (sid, AccessControlType.Allow);

    [Fact]
    public void 本人所有で本人だけに開いていれば通す() =>
        Assert.Null(PipeOwnershipCheck.Reject(Me, Me, new[] { Allow(Me) }));

    [Fact]
    public void Administrators_所有でも本人宛てなら通す()
    {
        // 管理者で動くデーモンが本人 SID 宛てに ACL を組んだ形
        Assert.Null(PipeOwnershipCheck.Reject(Me, Admins, new[] { Allow(Me), Allow(Admins), Allow(System) }));
    }

    [Fact]
    public void 別ユーザーが所有していれば弾く() =>
        Assert.Contains("所有者", PipeOwnershipCheck.Reject(Me, Other, new[] { Allow(Me) }));

    [Fact]
    public void 別ユーザーに開いていれば弾く() =>
        Assert.Contains("開かれています", PipeOwnershipCheck.Reject(Me, Me, new[] { Allow(Me), Allow(Other) }));

    [Fact]
    public void Everyone_に開いていれば弾く() =>
        Assert.NotNull(PipeOwnershipCheck.Reject(Me, Me, new[] { Allow(Everyone) }));

    [Fact]
    public void 拒否の規則は誰宛てでも気にしない() =>
        Assert.Null(PipeOwnershipCheck.Reject(Me, Me, new[] { Allow(Me), (Everyone, AccessControlType.Deny) }));

    [Fact]
    public void 所有者が読めなければ弾く() =>
        Assert.NotNull(PipeOwnershipCheck.Reject(Me, null, new[] { Allow(Me) }));
}
