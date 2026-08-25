namespace Stakeout.Rpc;

/// <summary>
/// トランスポートの取り決め（design.md §7）。
/// パイプ名は Daemon と Cli の両方が知る必要があるので、契約側に置く。
/// </summary>
public static class RpcTransport
{
    /// <summary>パイプ名を上書きする環境変数（テスト用。design.md §8.1）。</summary>
    public const string PipeEnvironmentVariable = "STAKEOUT_PIPE";

    /// <summary>
    /// 名前付きパイプ名。ユーザーごとに分ける。
    /// ACL は接続側・待ち受け側の PipeOptions.CurrentUserOnly で現在ユーザーのみに絞る。
    /// </summary>
    public static string PipeName =>
        Environment.GetEnvironmentVariable(PipeEnvironmentVariable) is { Length: > 0 } overridden
            ? overridden
            : $"stakeout-{Environment.UserName}";

    /// <summary>stakeout の状態を置くディレクトリ（%LOCALAPPDATA%\stakeout）。</summary>
    public static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "stakeout");

    /// <summary>
    /// デーモンが起動に失敗した理由を書き置くファイル。
    ///
    /// 標準エラーで渡さないのは、子プロセスの標準ストリームをリダイレクトすると
    /// CreateProcess がハンドル継承つきで呼ばれ、stakeout 自身の標準出力まで
    /// デーモンに引き継がれてしまうためである。そうなると stakeout が終了しても
    /// 出力パイプが閉じず、読んでいる側が永久に待つ。
    /// </summary>
    public static string StartupErrorFile => Path.Combine(StateRoot, "startup-error.txt");
}
