using System.Text.Json;
using Stakeout.Core;
using Stakeout.Rpc;

namespace Stakeout.Core.Tests;

public sealed class SessionLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stakeout-log-").FullName;

    /// <summary>まだ開かれているログを読む。共有を明示しないと自分の書き込みハンドルとぶつかる。</summary>
    private static JsonElement[] ReadEntries(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd()
                     .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.Trim())
                     .Where(l => l.Length > 0)
                     .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                     .ToArray();
    }

    [Fact]
    public void 一行一エントリで連番が振られる()
    {
        var path = WriteSample();
        var entries = ReadEntries(path);

        Assert.Equal(3, entries.Length);
        Assert.Equal(new[] { 1, 2, 3 }, entries.Select(e => e.GetProperty("seq").GetInt32()));
    }

    [Fact]
    public void 要求と応答が種別付きで残る()
    {
        var path = WriteSample();
        var entries = ReadEntries(path);

        Assert.Equal(LogKinds.Daemon, entries[0].GetProperty("kind").GetString());
        Assert.Equal(LogKinds.RpcRequest, entries[1].GetProperty("kind").GetString());
        Assert.Equal(LogKinds.RpcResponse, entries[2].GetProperty("kind").GetString());
        Assert.Equal(RpcMethods.DaemonStatus, entries[2].GetProperty("method").GetString());
        Assert.True(entries[2].GetProperty("durationMs").GetDouble() >= 0);
    }

    [Fact]
    public void ペイロードは_camelCase_で残る()
    {
        // ワイヤ表現と食い違うと、ログを見ながらクエリを書くときに毎回つまずく
        var path = WriteSample();
        var result = ReadEntries(path)[2].GetProperty("result").GetString();

        Assert.NotNull(result);
        Assert.Contains("\"version\"", result);
        Assert.DoesNotContain("\"Version\"", result);
    }

    [Fact]
    public void 長すぎるペイロードは切って_truncated_が立つ()
    {
        // Target のメモリ内容をそのまま残さないための上限でもある（design.md §21）
        var path = Path.Combine(_dir, "big");
        using (var log = SessionLog.Open(path, "big", DateTimeOffset.Now))
        {
            log.RpcResponse("vars.dump", new { blob = new string('x', SessionLog.MaxFieldBytes * 2) }, null, 1.0);

            var entry = ReadEntries(log.Path)[0];
            Assert.True(entry.GetProperty("truncated").GetBoolean());
            Assert.Equal(SessionLog.MaxFieldBytes, entry.GetProperty("result").GetString()!.Length);
        }
    }

    [Fact]
    public void 値が無いフィールドは書き出されない()
    {
        var path = Path.Combine(_dir, "sparse");
        using var log = SessionLog.Open(path, "sparse", DateTimeOffset.Now);
        log.Daemon("started");

        var entry = ReadEntries(log.Path)[0];

        Assert.False(entry.TryGetProperty("method", out _));
        Assert.False(entry.TryGetProperty("result", out _));
    }

    [Fact]
    public void ファイル名は日付とセッション_ID_になる()
    {
        var when = new DateTimeOffset(2026, 8, 25, 1, 2, 3, TimeSpan.FromHours(9));
        using var log = SessionLog.Open(Path.Combine(_dir, "named"), "abcd1234", when);

        Assert.Equal("20260825-abcd1234.jsonl", Path.GetFileName(log.Path));
    }

    private string WriteSample()
    {
        var dir = Path.Combine(_dir, "sample");
        using var log = SessionLog.Open(dir, "sample01", DateTimeOffset.Now);

        log.Daemon("stakeout started");
        log.RpcRequest(RpcMethods.DaemonStatus, null);
        log.RpcResponse(RpcMethods.DaemonStatus, new DaemonPong("1.0.0", 1234), null, 0.5);

        return log.Path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
