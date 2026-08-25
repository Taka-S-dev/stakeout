namespace Stakeout.Rpc;

/// <summary>
/// RPC のメソッド名（design.md §7.1）。
/// 文字列を直接書かず必ずここを経由することで、Daemon と Cli のずれをコンパイル時に防ぐ。
/// 実装済みのものだけを置く。フェーズが進むたびに足す。
/// </summary>
public static class RpcMethods
{
    /// <summary>稼働確認。引数なし。</summary>
    public const string DaemonPing = "daemon.ping";

    /// <summary>デーモンの状態とセッション一覧。</summary>
    public const string DaemonStatus = "daemon.status";

    /// <summary>デーモンを停止する。全セッションをデタッチしてから終了する。</summary>
    public const string DaemonShutdown = "daemon.shutdown";

    /// <summary>アタッチ可能なプロセス一覧（allowlist 適用後）。</summary>
    public const string TargetList = "target.list";

    public const string SessionAttach = "session.attach";

    public const string SessionDetach = "session.detach";

    public const string SessionInfo = "session.info";

    public const string ExecContinue = "exec.continue";

    public const string ExecPause = "exec.pause";

    public const string ExecStep = "exec.step";

    /// <summary>停止を待つ。期限内に停止しなくてもエラーにはしない。</summary>
    public const string ExecWait = "exec.wait";

    public const string ThreadsList = "threads.list";

    public const string ThreadsSelect = "threads.select";

    public const string ThreadsFreeze = "threads.freeze";

    public const string StackGet = "stack.get";

    public const string VarsScope = "vars.scope";

    public const string VarsEval = "vars.eval";

    public const string VarsExpand = "vars.expand";

    /// <summary>メモリを読む（design.md §10.2）。</summary>
    public const string MemRead = "mem.read";

    public const string BreakpointSet = "bp.set";

    public const string BreakpointRemove = "bp.remove";

    public const string BreakpointList = "bp.list";

    public const string BreakpointClear = "bp.clear";

    public const string BreakpointExceptions = "bp.exceptions";

    /// <summary>構造体の再帰展開（design.md §10.7）。</summary>
    public const string VarsDump = "vars.dump";

    public const string CompositeRunUntil = "composite.runUntil";

    public const string CompositeWatchUntilChange = "composite.watchUntilChange";

    public const string CompositeTraceExpression = "composite.traceExpression";

    public const string CompositeTaskMap = "composite.taskMap";

    /// <summary>打ち切られた応答の続きを取る（design.md §8.5）。</summary>
    public const string CursorNext = "cursor.next";

    public const string CodeDefinitions = "code.def";

    public const string CodeReferences = "code.refs";

    /// <summary>そのシンボルに書いていそうな場所（design.md §13）。</summary>
    public const string CodeWriters = "code.writers";

    /// <summary>静的な候補と動的な書き込みを突き合わせる（design.md §9.5）。</summary>
    public const string CompositeFindCorruption = "composite.findCorruption";

    /// <summary>セッションログの末尾を読む（design.md §14）。</summary>
    public const string LogTail = "log.tail";

    /// <summary>実装前に確認すべき項目に答える（design.md §3.2）。</summary>
    public const string DaemonDoctor = "daemon.doctor";
}
