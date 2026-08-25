using System.Text.RegularExpressions;
using Stakeout.Core;
using Stakeout.Rpc;
using EnvDTE;
using DteBreakpoint = EnvDTE.Breakpoint;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// ブレークポイントの追跡（ADR 0006）。
///
/// <c>Breakpoints.Add</c> の戻り値を信用しない。実測では、ブレークポイントを
/// 作成しておきながら空のコレクションを返すことがあった。信用すると
/// 「作れていない」と誤認して重複作成し、4 本しかないハードウェアデータ
/// ブレークポイントを食い潰す。
///
/// そのため、追加の前後で <c>Debugger.Breakpoints</c> をスナップショットし、
/// その差分を「実際に作成されたもの」として扱う。
/// </summary>
internal sealed partial class BreakpointTracker
{
    [GeneratedRegex(@"0x[0-9A-Fa-f]+")]
    private static partial Regex AddressPattern();

    private readonly Dictionary<int, Entry> _byId = new();
    private int _nextId = 1;

    private sealed record Entry(DteBreakpoint Breakpoint, BreakpointKind Kind, string Location, string? Condition);

    /// <summary>stakeout が張ったデータブレークポイントの本数。</summary>
    public int DataBreakpointCount => _byId.Values.Count(e => e.Kind == BreakpointKind.Data);

    /// <summary>追加前のスナップショットを取る。</summary>
    public static HashSet<DteBreakpoint> Snapshot(Breakpoints breakpoints)
    {
        var set = new HashSet<DteBreakpoint>();
        foreach (DteBreakpoint bp in breakpoints)
        {
            set.Add(bp);
        }

        return set;
    }

    /// <summary>スナップショット以降に増えたものを返す。</summary>
    public static List<DteBreakpoint> Added(Breakpoints breakpoints, HashSet<DteBreakpoint> before)
    {
        var added = new List<DteBreakpoint>();
        foreach (DteBreakpoint bp in breakpoints)
        {
            if (!before.Contains(bp))
            {
                added.Add(bp);
            }
        }

        return added;
    }

    /// <summary>作成されたブレークポイントを登録し、stakeout 側の ID を振る。</summary>
    public Rpc.Breakpoint Register(DteBreakpoint bp, BreakpointKind kind, string location, string? condition)
    {
        var id = _nextId++;

        // Tag に印を付けておく。停止したときに VS が返すのは、こちらが作った
        // ブレークポイントそのものではなく、それに束縛された子であることがある。
        // Tag は子にも引き継がれるので、名前の一致に頼らず照合できる
        try
        {
            bp.Tag = TagFor(id);
        }
        catch (Exception ex) when (ex is not BackendException)
        {
            // Tag が付かなくても、名前と位置での照合にフォールバックする
        }

        _byId[id] = new Entry(bp, kind, location, condition);
        return Describe(id, _byId[id]);
    }

    private static string TagFor(int id) => $"stakeout:{id}";

    public bool TryGet(int id, out DteBreakpoint breakpoint)
    {
        if (_byId.TryGetValue(id, out var entry))
        {
            breakpoint = entry.Breakpoint;
            return true;
        }

        breakpoint = null!;
        return false;
    }

    public void Remove(int id) => _byId.Remove(id);

    public IReadOnlyList<int> Ids => _byId.Keys.ToArray();

    public IReadOnlyList<Rpc.Breakpoint> List() =>
        _byId.Select(kv => Describe(kv.Key, kv.Value)).ToArray();

    /// <summary>
    /// stakeout の ID を、VS のブレークポイントから逆引きする。停止理由の特定に使う。
    ///
    /// VS が <c>BreakpointLastHit</c> で返すのは、こちらが作ったブレークポイントに
    /// 束縛された子であることがある。Tag → 親の Tag → 位置、の順に照合する。
    /// </summary>
    public int? IdOf(DteBreakpoint? bp)
    {
        if (bp is null)
        {
            return null;
        }

        if (ParseTag(Safe(() => bp.Tag)) is { } tagged)
        {
            return tagged;
        }

        if (ParseTag(Safe(() => bp.Parent?.Tag)) is { } parentTagged)
        {
            return parentTagged;
        }

        foreach (var (id, entry) in _byId)
        {
            if (SameBreakpoint(entry.Breakpoint, bp))
            {
                return id;
            }
        }

        return null;
    }

    private static int? ParseTag(string? tag)
    {
        if (tag is null || !tag.StartsWith("stakeout:", StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(tag[5..], out var id) ? id : null;
    }

    public BreakpointKind? KindOf(int id) => _byId.TryGetValue(id, out var entry) ? entry.Kind : null;

    /// <summary>
    /// データブレークポイントが意図したアドレスを監視しているか検証する（ADR 0006 の罠 3）。
    /// </summary>
    public static void VerifyDataAddress(DteBreakpoint bp, string expectedAddress) =>
        BreakpointAddressCheck.Verify(Safe(() => bp.Name) ?? string.Empty, expectedAddress);

    private static bool SameBreakpoint(DteBreakpoint a, DteBreakpoint b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (Safe(() => a.Name) is { Length: > 0 } nameA && nameA == Safe(() => b.Name))
        {
            return true;
        }

        // 名前は表示用の文言で、束縛の前後で変わりうる。位置でも照合する
        if (Safe(() => a.File) is { Length: > 0 } file
            && file == Safe(() => b.File)
            && Safe(() => a.FileLine, -1) == Safe(() => b.FileLine, -2))
        {
            return true;
        }

        return Safe(() => a.FunctionName) is { Length: > 0 } function
               && function == Safe(() => b.FunctionName);
    }

    private static Rpc.Breakpoint Describe(int id, Entry entry) => new(
        BreakpointId: id,
        Kind: entry.Kind,
        Location: entry.Location,
        Condition: entry.Condition,
        HitCount: Safe(() => entry.Breakpoint.CurrentHits),
        BreakWhenHit: true,
        TraceExpressions: null,
        Verified: Safe(() => entry.Breakpoint.Enabled, false),
        VerifyMessage: Safe(() => entry.Breakpoint.Name));

    private static T Safe<T>(Func<T> get, T fallback = default!)
    {
        try
        {
            return get();
        }
        catch (Exception ex) when (ex is not BackendException)
        {
            return fallback;
        }
    }
}
