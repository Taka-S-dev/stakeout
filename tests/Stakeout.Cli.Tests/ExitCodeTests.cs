using Stakeout.Cli;
using Stakeout.Rpc;

namespace Stakeout.Cli.Tests;

/// <summary>
/// 終了コードの対応（design.md §8.3）。
/// エージェントは終了コードだけで分岐するので、ここがずれると調査の型が壊れる。
/// </summary>
public sealed class ExitCodeTests
{
    [Theory]
    [InlineData(ErrorCodes.Usage, ExitCode.UsageError)]
    [InlineData(ErrorCodes.Timeout, ExitCode.Timeout)]
    [InlineData(ErrorCodes.Precondition, ExitCode.Precondition)]
    [InlineData(ErrorCodes.NotAttached, ExitCode.Precondition)]
    [InlineData(ErrorCodes.NotFound, ExitCode.NotFound)]
    [InlineData(ErrorCodes.Denied, ExitCode.NotFound)]
    [InlineData(ErrorCodes.Backend, ExitCode.RuntimeError)]
    [InlineData(ErrorCodes.Unsupported, ExitCode.RuntimeError)]
    [InlineData(ErrorCodes.NotConfigured, ExitCode.RuntimeError)]
    [InlineData(ErrorCodes.Internal, ExitCode.RuntimeError)]
    [InlineData(ErrorCodes.DaemonUnreachable, ExitCode.RuntimeError)]
    public void エラーコードが終了コードに対応する(string code, int expected)
    {
        Assert.Equal(expected, ExitCode.FromErrorCode(code));
    }

    [Fact]
    public void 未知のエラーコードは実行時エラーになる()
    {
        Assert.Equal(ExitCode.RuntimeError, ExitCode.FromErrorCode("SOMETHING_NEW"));
    }

    [Fact]
    public void 停止していないことは実行時エラーではなくタイムアウトで表す()
    {
        // wait が停止を捉えられなかった場合、エージェントは再度 wait する（design.md §8.6）。
        // 実行時エラーと同じコードにすると、その分岐ができなくなる
        Assert.Equal(3, ExitCode.Timeout);
        Assert.NotEqual(ExitCode.RuntimeError, ExitCode.Timeout);
    }
}
