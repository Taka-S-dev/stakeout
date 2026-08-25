using System.Text;
using Stakeout.Rpc;

namespace Stakeout.Core;

/// <summary>
/// 読み取ったバイト列を、人とエージェントの両方が読める形にする。
/// Backend に依存しない。
/// </summary>
public static class MemoryFormat
{
    /// <summary>バイト列を <see cref="MemoryBlock"/> にする。</summary>
    public static MemoryBlock ToBlock(ulong address, int requested, byte[] bytes) =>
        new(address, requested, bytes.Length, Hex(bytes), Ascii(bytes));

    /// <summary>"48 89 5c 24" のような 16 進表記。</summary>
    public static string Hex(byte[] bytes) =>
        string.Join(' ', bytes.Select(b => b.ToString("x2")));

    /// <summary>印字できる ASCII はそのまま、それ以外は '.' にする。</summary>
    public static string Ascii(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length);

        foreach (var b in bytes)
        {
            builder.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
        }

        return builder.ToString();
    }

    /// <summary>
    /// 1 行 16 バイトの hexdump にする。CLI が人向けに出すときに使う。
    /// </summary>
    public static IEnumerable<string> Lines(ulong address, byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var row = bytes[offset..Math.Min(offset + 16, bytes.Length)];

            yield return $"{address + (ulong)offset:x16}  {Hex(row).PadRight(47)}  {Ascii(row)}";
        }
    }
}
