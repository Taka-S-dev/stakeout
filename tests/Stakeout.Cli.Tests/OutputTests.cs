using System.Text.Json;
using Stakeout.Cli;
using Stakeout.Client;
using Stakeout.Rpc;

namespace Stakeout.Cli.Tests;

/// <summary>
/// 出力の切り替え（design.md §8.2 / ADR 0015）。
///
/// 成功は stdout、エラーは stderr。<c>--json</c> では封筒ごと出す。
/// 打ち切りとカーソルを別の流れに出すと、出力をまとめて受け取る側で JSON が壊れる。
/// </summary>
public sealed class OutputTests
{
    private static (string Out, string Err, int Code) Capture(Func<Output, int> body, bool json)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var code = body(new Output(json));
            return (stdout.ToString(), stderr.ToString(), code);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void json_モードでは封筒に包んで_stdout_に出す()
    {
        var data = Parse("""{"pid":42,"backend":"envdte"}""");

        var (stdout, stderr, code) = Capture(o => o.WriteSuccess(data, _ => { }), json: true);

        Assert.Equal(ExitCode.Ok, code);
        Assert.Empty(stderr);

        var envelope = Parse(stdout);
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(42, envelope.GetProperty("data").GetProperty("pid").GetInt32());
    }

    [Fact]
    public void 打ち切られたらカーソルが同じ封筒に入る()
    {
        var data = Parse("[1,2]");

        var (stdout, stderr, _) = Capture(
            o => o.WriteSuccess(data, _ => { }, truncated: true, cursor: "abc123"), json: true);

        Assert.Empty(stderr);

        var envelope = Parse(stdout);
        Assert.True(envelope.GetProperty("truncated").GetBoolean());
        Assert.Equal("abc123", envelope.GetProperty("cursor").GetString());
    }

    [Fact]
    public void 打ち切られていなければ余計な項目を出さない()
    {
        var data = Parse("[1,2]");

        var (stdout, _, _) = Capture(o => o.WriteSuccess(data, _ => { }), json: true);

        var envelope = Parse(stdout);
        Assert.False(envelope.TryGetProperty("cursor", out _));
        Assert.False(envelope.TryGetProperty("truncated", out _));
    }

    [Fact]
    public void テキストモードでは書き手の整形を使う()
    {
        var data = Parse("""{"pid":42}""");

        var (stdout, _, _) = Capture(
            o => o.WriteSuccess(data, d => Console.WriteLine($"pid={Output.Get(d, "pid")}")),
            json: false);

        Assert.Equal("pid=42", stdout.Trim());
    }

    [Fact]
    public void テキストモードでは続きの取り方を案内する()
    {
        var data = Parse("[1,2]");

        var (stdout, _, _) = Capture(
            o => o.WriteSuccess(data, _ => Console.WriteLine("body"), truncated: true, cursor: "abc123"),
            json: false);

        Assert.Contains("body", stdout);
        Assert.Contains("--cursor abc123", stdout);
    }

    [Fact]
    public void エラーは常に_stderr_に出て終了コードに写る()
    {
        var error = new StakeoutError(ErrorCodes.Precondition, "Target is running.", "stakeout pause を実行してください。");

        var (stdout, stderr, code) = Capture(o => o.WriteError(error), json: false);

        Assert.Empty(stdout);
        Assert.Equal(ExitCode.Precondition, code);
        Assert.Contains("PRECONDITION", stderr);
        Assert.Contains("stakeout pause", stderr);
    }

    [Fact]
    public void json_モードのエラーも_stderr_に出る()
    {
        var error = new StakeoutError(ErrorCodes.NotFound, "見つかりません", "候補を確認してください");

        var (stdout, stderr, code) = Capture(o => o.WriteError(error), json: true);

        Assert.Empty(stdout);
        Assert.Equal(ExitCode.NotFound, code);

        var envelope = Parse(stderr);
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("NOT_FOUND", envelope.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void 日本語をエスケープせずに出す()
    {
        var error = new StakeoutError(ErrorCodes.NotFound, "見つかりません", "候補を確認してください");

        var (_, stderr, _) = Capture(o => o.WriteError(error), json: true);

        Assert.Contains("見つかりません", stderr);
        Assert.DoesNotContain("\\u", stderr);
    }

    [Fact]
    public void 応答にエラーが入っていなければ内部エラーとして扱う()
    {
        var result = new CliResult(false, default, null, false, null);

        var (_, stderr, code) = Capture(o => o.Write(result, _ => { }), json: false);

        Assert.Equal(ExitCode.RuntimeError, code);
        Assert.Contains(ErrorCodes.Internal, stderr);
    }

    [Fact]
    public void 打ち切られた結果は本文とカーソルの両方を運ぶ()
    {
        var result = new CliResult(true, Parse("[1]"), null, true, "cur");

        var (stdout, _, code) = Capture(o => o.Write(result, _ => { }), json: true);

        Assert.Equal(ExitCode.Ok, code);
        Assert.Equal("cur", Parse(stdout).GetProperty("cursor").GetString());
    }

    [Fact]
    public void テキストモードの_Line_は_json_モードでは黙る()
    {
        var (jsonOut, _, _) = Capture(o => { o.Line("進捗"); return ExitCode.Ok; }, json: true);
        var (textOut, _, _) = Capture(o => { o.Line("進捗"); return ExitCode.Ok; }, json: false);

        Assert.Empty(jsonOut);
        Assert.Equal("進捗", textOut.Trim());
    }

    [Fact]
    public void 無い項目は既定値になる()
    {
        var data = Parse("""{"a":null}""");

        Assert.Equal("-", Output.Get(data, "missing"));
        Assert.Equal("-", Output.Get(data, "a"));
        Assert.Equal("0", Output.Get(data, "missing", "0"));
    }
}
