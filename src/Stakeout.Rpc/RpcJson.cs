using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stakeout.Rpc;

/// <summary>
/// ワイヤ表現の取り決め。Daemon と Cli が同じ設定を使う。
///
/// 列挙は**文字列**で運ぶ。数値のまま出すと、CLI の JSON 出力に
/// <c>"state": 0</c> のような値が並び、エージェントも人間も意味を読み取れない。
/// </summary>
public static class RpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
