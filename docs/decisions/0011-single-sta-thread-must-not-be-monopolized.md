# 0011. 単一の STA スレッドを待ちで占有しない

- 状態: 受理
- 日付: 2026-08-25
- 関連: design.md §7.2, §10.2 / ADR 0008

## 発見した事実

DTE の呼び出しはすべて 1 本の STA スレッドで行う（design.md §10.2）。
Phase 1 の最初の実装では、停止待ちのポーリングループを**その STA スレッド上の
1 つの作業項目として**回していた。

```csharp
// 誤り: 待ちの全体が 1 つの作業項目になっている
public Task<bool> WaitUntilAsync(...) =>
    InvokeAsync(() =>
    {
        while (!condition()) { PumpMessages(); Thread.Sleep(interval); }
        return true;
    });
```

この実装は動くが、**待っている間 STA スレッドが完全に塞がる**。
統合検証で、`stakeout wait` の実行中に `stakeout daemon status` が **13.9 秒** 返らなかった。

design.md §7.2 は「`exec.wait` は例外的に、他の読み取り系と並行できる」と定めている。
RPC の層では確かに直列化していなかった（`RunUnserializedAsync`）が、
その下の STA スレッドで詰まっていた。**並行性は一番細いところで決まる。**

## 決定

1. **待ちの全体を 1 つの作業項目にしない。** 1 回の条件判定だけを STA に投げ、
   間隔は STA の外で待つ。

   ```csharp
   while (true)
   {
       if (await InvokeAsync(condition, ct)) return true;
       if (期限切れ) return false;
       await Task.Delay(interval, ct);   // STA を解放したまま待つ
   }
   ```

2. **状態の問い合わせは有限時間で諦める。** `daemon.status` が返す
   セッション状態は「あれば嬉しい」情報である。取得に 2 秒以上かかるなら
   最後に分かっている状態を返す。状態を知りたいときほど返らない道具にしない。

3. 今後、STA 上で長く回るループを書かない。ポーリングは必ず
   「1 回だけ投げて外で待つ」形にする。

## 影響

- `StaDispatcher.WaitUntilAsync` を非同期ループに変更した
- `StaDispatcher.InvokeWithFallback` を追加し、`daemon.status` の状態取得に使う
- `tests/integration/phase1.ps1` に「停止を待っている間も status が返る」検証を追加した
  （5 秒以内に返ること）
