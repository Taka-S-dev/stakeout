using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stakeout.Mcp;
using Stakeout.Rpc;

namespace Stakeout.Mcp.Tests;

/// <summary>
/// MCP に見せる道具の定義（design.md §20 Phase 7）。
///
/// ツール定義はクライアントの文脈を毎回消費する。
/// 大きさと中身は、増やすたびに測らないと気づかないうちに膨らむ。
/// </summary>
public sealed class McpToolsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void 定義の合計が_4_KiB_を超えない()
    {
        // 実際に送られる形で測る。本体と別の測り方をすると、
        // 片方だけが上限を守っているつもりになる
        var bytes = McpTools.DescribeByteCount();

        Assert.True(bytes <= 4096, $"ツール定義が {bytes} バイトある（上限 4096）");
    }

    [Fact]
    public void 低レベル操作は載せない()
    {
        // 細かい操作が要るなら CLI を使えばよい（design.md §20 Phase 7）
        var names = McpTools.All.Select(t => t.Name).ToArray();

        Assert.DoesNotContain("stakeout_step", names);
        Assert.DoesNotContain("stakeout_bp_set", names);
        Assert.DoesNotContain("stakeout_thread_freeze", names);
    }

    [Fact]
    public void 調査の主線が揃っている()
    {
        var names = McpTools.All.Select(t => t.Name).ToArray();

        Assert.Contains("stakeout_attach", names);
        Assert.Contains("stakeout_run_until", names);
        Assert.Contains("stakeout_watch_until_change", names);
        Assert.Contains("stakeout_find_corruption", names);
        Assert.Contains("stakeout_dump", names);
    }

    [Fact]
    public void 名前が重複していない()
    {
        var names = McpTools.All.Select(t => t.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void すべての道具に説明とスキーマがある()
    {
        foreach (var tool in McpTools.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} に説明が無い");
            Assert.Equal("object", tool.Schema["type"]?.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(tool.Method), $"{tool.Name} に RPC メソッドが無い");
        }
    }

    [Fact]
    public void 必須の引数がスキーマに現れる()
    {
        var runUntil = McpTools.All.Single(t => t.Name == "stakeout_run_until");
        var required = runUntil.Schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("location", required);
        Assert.DoesNotContain("condition", required);
    }

    [Fact]
    public void 必須の引数が欠けていれば断る()
    {
        var runUntil = McpTools.All.Single(t => t.Name == "stakeout_run_until");

        var ex = Assert.Throws<McpException>(() => runUntil.Build(Args("{}")));

        Assert.Contains("location", ex.Message);
    }

    [Fact]
    public void 引数を_RPC_の要求に写せる()
    {
        var runUntil = McpTools.All.Single(t => t.Name == "stakeout_run_until");

        var request = (RunUntilRequest)runUntil.Build(Args(
            """{"location":"a.c:10","condition":"x == 1","exprs":["x","y"],"timeoutSec":30}"""))!;

        Assert.Equal("a.c:10", request.Location);
        Assert.Equal("x == 1", request.Condition);
        Assert.Equal(new[] { "x", "y" }, request.Exprs);
        Assert.Equal(30000, request.TimeoutMs);
    }

    [Fact]
    public void 省略された引数は既定値になる()
    {
        var dump = McpTools.All.Single(t => t.Name == "stakeout_dump");

        var request = (DumpRequest)dump.Build(Args("""{"expr":"g_ctx"}"""))!;

        Assert.Equal(2, request.Depth);
        Assert.Equal(50, request.MaxItems);
    }

    [Fact]
    public void 引数を取らない道具は_null_を渡す()
    {
        var taskMap = McpTools.All.Single(t => t.Name == "stakeout_task_map");

        Assert.Null(taskMap.Build(Args("{}")));
    }

    [Fact]
    public void attach_は_pid_と_name_のどちらでも組み立てられる()
    {
        var attach = McpTools.All.Single(t => t.Name == "stakeout_attach");

        var byPid = (AttachRequest)attach.Build(Args("""{"pid":1234}"""))!;
        var byName = (AttachRequest)attach.Build(Args("""{"name":"Harness"}"""))!;

        Assert.Equal(1234, byPid.Pid);
        Assert.Null(byPid.Name);
        Assert.Equal("Harness", byName.Name);
    }
}
