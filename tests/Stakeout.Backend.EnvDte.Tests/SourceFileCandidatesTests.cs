using Stakeout.Backend.EnvDte;
using Xunit;

namespace Stakeout.Backend.EnvDte.Tests;

/// <summary>
/// ファイル名だけで指定された行ブレークポイントの、張り直し先の候補（ADR 0023）。
///
/// 評価スイートでは、前のケースで開いた同名の nativelib.c（すでに削除済み）に
/// ブレークポイントが解決され、一度も止まらないまま調査が 10 分近く迷走した。
/// </summary>
public sealed class SourceFileCandidatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stakeout-srccand-" + Guid.NewGuid().ToString("N"));

    public SourceFileCandidatesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void フルパスが指定されていれば推測しない()
    {
        var path = Touch(@"tree\NativeLib\nativelib.c");

        Assert.Empty(SourceFileCandidates.For(path, Path.Combine(_root, "tree"), new[] { path }));
    }

    [Fact]
    public void 索引ルートの同名ファイルを開いているドキュメントより先に返す()
    {
        var inTree = Touch(@"tree\NativeLib\nativelib.c");
        var elsewhere = Touch(@"elsewhere\nativelib.c");

        var candidates = SourceFileCandidates.For("nativelib.c", Path.Combine(_root, "tree"), new[] { elsewhere });

        Assert.Equal(new[] { inTree, elsewhere }, candidates);
    }

    [Fact]
    public void 消えたドキュメントは候補にしない()
    {
        var deleted = Path.Combine(_root, @"old-workspace\nativelib.c");

        Assert.Empty(SourceFileCandidates.For("nativelib.c", sourceRoot: null, new[] { deleted }));
    }

    [Fact]
    public void 相対パスは末尾で照合する()
    {
        var wanted = Touch(@"tree\NativeLib\nativelib.c");
        Touch(@"tree\Other\nativelib.c");

        var candidates = SourceFileCandidates.For("NativeLib/nativelib.c", Path.Combine(_root, "tree"), Array.Empty<string>());

        Assert.Equal(new[] { wanted }, candidates);
    }

    [Fact]
    public void 同じファイルを二度返さない()
    {
        var inTree = Touch(@"tree\nativelib.c");

        var candidates = SourceFileCandidates.For("nativelib.c", Path.Combine(_root, "tree"), new[] { inTree.ToUpperInvariant() });

        Assert.Single(candidates);
    }
}
