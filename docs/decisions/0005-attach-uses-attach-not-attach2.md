# 0005. アタッチは `Attach2` ではなく `Attach` を使う

- 状態: 受理
- 日付: 2026-08-25
- 検証: S2（spike/results/spike-final.json）
- 関連: design.md §10.4, §7.1（`session.attach` の `engines`）

## 背景

design.md §10.4 は `Process2.Attach2("Native")` でネイティブ限定アタッチを行うと定めている。
これはマネージド（C#）側を巻き込まないために重要な指定である。

## 発見した事実

検証環境（VS 18.0 / .NET 8 / x64 のネイティブ Target）で、
`Process2.Attach2` は**エンジン指定の形式によらず** `0x8971001E` で失敗した。

| 呼び出し | 結果 |
|---|---|
| `Attach2("Native")` | `COMException 0x8971001E` |
| `Attach2(new[] { "Native" })` | `COMException 0x8971001E` |
| `Attach2("Native Code")` | `COMException 0x8971001E` |
| `Attach()` | **成功** |

`Attach()` で接続したセッションでは、期待どおりネイティブのスタック・式評価が使えた
（`nl_update_state` で停止し、`g_ctx` の構造体メンバを読めた）。Target は C 実行ファイルであり、
マネージドコードを含まないため、エンジン自動選択でもネイティブになったと考えられる。

`0x8971001E` の正確な意味は特定できていない。VS のバージョン差か、
エンジン名の文字列が VS 18.0 で変わった可能性がある。

## 決定

1. アタッチは次の順で試し、最初に成功したものを使う。
   1. `Process2.Attach2(engines)` — 呼び出し側が `engines` を明示した場合のみ
   2. `Process2.Attach2("Native")`
   3. `Process.Attach()`
2. どの方法で成功したかを `SessionInfo` に含め、`stakeout attach --json` の出力と
   セッションログに残す。`Attach()` にフォールバックした場合は
   「エンジンを明示できていない」ことが分かる warning を結果に付ける。
3. `session.attach` の `engines` 引数は残す。指定されたのに `Attach2` が失敗した場合は、
   **黙って `Attach()` にフォールバックしない**。エンジン指定は「マネージドを巻き込まない」という
   意図の表明であり、それを無視して繋ぐと調査の前提が崩れる。`UNSUPPORTED` で返す。
4. C# ホスト + C DLL という本来の Target では、エンジン自動選択が
   マネージドを巻き込む可能性がある。**この点は VS2019 環境で再検証が必要**であり、
   Phase 1 の完了条件に「`Attach()` 経由でもスタックに `[Managed to Native Transition]` が
   現れないこと」を追加する。現れる場合は付録 B（混合モード）の扱いに倒す。

## 影響

- design.md §10.4 の「`Attach2("Native")`」を上記の 3 段フォールバックに改める
- design.md §20 Phase 1 の完了条件に、エンジン確認の項目を追加する
- 未解決: `0x8971001E` の原因。VS2019 で同じ失敗をするかは未確認
