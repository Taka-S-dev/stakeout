using EnvDTE;

namespace Stakeout.Backend.EnvDte;

/// <summary>
/// 1 回の停止の間だけ有効な参照表。
///
/// フレーム ID と変数参照は、その停止の中でしか意味を持たない。
/// 実行を再開したらすべて捨てる。持ち越すと、別の停止の古いフレームを
/// 指したまま値を読んでしまい、原因の分からない誤った調査結果になる。
/// </summary>
internal sealed class StopScope
{
    private readonly Dictionary<int, FrameRef> _frames = new();
    private readonly Dictionary<int, Expression> _expressions = new();

    private int _nextFrameId = 1;
    private int _nextExpressionId = 1;

    /// <param name="ThreadId">このフレームを持つスレッド。</param>
    /// <param name="Depth">0 が最上段。</param>
    public sealed record FrameRef(int ThreadId, int Depth);

    public int AddFrame(int threadId, int depth)
    {
        var id = _nextFrameId++;
        _frames[id] = new FrameRef(threadId, depth);
        return id;
    }

    public FrameRef? Frame(int frameId) => _frames.GetValueOrDefault(frameId);

    /// <summary>子を持つ式を登録して参照番号を返す。子が無ければ 0。</summary>
    public int AddExpression(Expression expression, bool hasChildren)
    {
        if (!hasChildren)
        {
            return 0;
        }

        var id = _nextExpressionId++;
        _expressions[id] = expression;
        return id;
    }

    public Expression? Expression(int reference) => _expressions.GetValueOrDefault(reference);

    public void Clear()
    {
        _frames.Clear();
        _expressions.Clear();
        _nextFrameId = 1;
        _nextExpressionId = 1;
    }
}
