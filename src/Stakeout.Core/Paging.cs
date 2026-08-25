using System.Collections.Concurrent;
using System.Text.Json;

namespace Stakeout.Core;

/// <param name="Items">今回返す分。</param>
/// <param name="Rest">残り。空なら打ち切っていない。</param>
public sealed record Page<T>(IReadOnlyList<T> Items, IReadOnlyList<T> Rest)
{
    public bool Truncated => Rest.Count > 0;
}

/// <summary>
/// 応答の分割（design.md §8.5）。
/// 上限はバイト数で決める。件数で切ると、要素の大きさが読めない
/// <c>stack</c> や <c>dump</c> で上限が意味を持たなくなる。
/// </summary>
public static class Pager
{
    /// <summary>
    /// JSON 化したときの合計が <paramref name="maxBytes"/> を超えない範囲で先頭から詰める。
    /// 1 件目だけで超える場合でも、その 1 件は必ず返す。空の応答を返して
    /// 呼び出し側を無限ループさせるより、上限を破って 1 件返すほうがましである。
    /// </summary>
    public static Page<T> Split<T>(IReadOnlyList<T> items, int maxBytes, JsonSerializerOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (items.Count == 0)
        {
            return new Page<T>(Array.Empty<T>(), Array.Empty<T>());
        }

        // 配列そのものの区切り文字ぶん
        var used = 2;
        var taken = 0;

        foreach (var item in items)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(item, options).Length + 1;
            if (taken > 0 && used + size > maxBytes)
            {
                break;
            }

            used += size;
            taken++;
        }

        return new Page<T>(
            items.Take(taken).ToArray(),
            items.Skip(taken).ToArray());
    }
}

/// <summary>
/// カーソルの保管（design.md §8.5）。デーモン内に 10 分だけ残す。
/// 期限切れのカーソルは <c>NOT_FOUND</c> として扱い、hint で最初からやり直すよう促す。
/// </summary>
public sealed class CursorStore
{
    /// <summary>カーソルの既定の有効期間（design.md §8.5）。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;

    public CursorStore(TimeProvider? time = null, TimeSpan? ttl = null)
    {
        _time = time ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>残りを預けてカーソルを発行する。</summary>
    public string Create(object remainder)
    {
        Sweep();
        var cursor = Guid.NewGuid().ToString("n");
        _entries[cursor] = new Entry(remainder, _time.GetUtcNow() + _ttl);
        return cursor;
    }

    /// <summary>
    /// カーソルを引き換える。1 回で消える。
    /// 続きの続きが要る場合は、取り出した側が新しいカーソルを発行する。
    /// </summary>
    public bool TryTake(string cursor, out object? remainder)
    {
        Sweep();

        if (_entries.TryRemove(cursor, out var entry) && entry.ExpiresAt > _time.GetUtcNow())
        {
            remainder = entry.Remainder;
            return true;
        }

        remainder = null;
        return false;
    }

    /// <summary>保管中のカーソル数。テストと診断用。</summary>
    public int Count
    {
        get
        {
            Sweep();
            return _entries.Count;
        }
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(object Remainder, DateTimeOffset ExpiresAt);
}
