using System.IO.Pipes;
using System.Text.Json;
using Stakeout.Rpc;
using StreamJsonRpc;

namespace Stakeout.Daemon;

/// <summary>
/// 名前付きパイプの待ち受け（design.md §7）。
///
/// ACL は既定で <see cref="PipeOptions.CurrentUserOnly"/>。<c>pipe.allowUnelevatedClients</c> のときは
/// ユーザー本人の SID 宛てにする（<see cref="PipeAccess"/>、ADR 0024）。
/// 接続は複数受ける（CLI が並行して呼ぶため）。1 接続 = 1 JsonRpc。
/// </summary>
public sealed class PipeServer
{
    /// <summary>同時に待ち受ける接続数。CLI の並行呼び出しに余裕を持たせる。</summary>
    private const int MaxConcurrentConnections = 8;

    private readonly string _pipeName;
    private readonly DaemonState _state;

    public PipeServer(string pipeName, DaemonState state)
    {
        _pipeName = pipeName;
        _state = state;
    }

    /// <summary>停止が要求されるまで接続を受け付け続ける。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _state.ShutdownToken);

        var listeners = Enumerable
            .Range(0, MaxConcurrentConnections)
            .Select(_ => ListenLoopAsync(linked.Token))
            .ToArray();

        try
        {
            await Task.WhenAll(listeners);
        }
        catch (OperationCanceledException)
        {
            // 停止要求。正常系
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = PipeAccess.Create(_pipeName, MaxConcurrentConnections, _state.Config.Pipe.AllowUnelevatedClients);

                await pipe.WaitForConnectionAsync(ct);

                // 誰が繋いだかを必ず残す。ACL を本人宛てに緩めている構成では、これが監査の手がかりになる
                _state.Log.Daemon(PipeAccess.DescribeClient(pipe));
                await ServeAsync(pipe, ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (IOException ex)
            {
                // クライアントが途中で切れただけ。待ち受けは続ける
                _state.Log.Daemon($"pipe io: {ex.Message}");
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = RpcJson.Options,
        };

        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, formatter));
        rpc.AddLocalRpcTarget(new RpcHandlers(_state, _pipeName), null);
        rpc.StartListening();

        // 相手が切るか、停止が要求されるまで
        var completion = rpc.Completion;
        var cancelled = Task.Delay(Timeout.Infinite, ct);
        await Task.WhenAny(completion, cancelled);

        if (completion.IsFaulted)
        {
            _state.Log.Daemon($"rpc faulted: {completion.Exception?.GetBaseException().Message}");
        }
    }
}
