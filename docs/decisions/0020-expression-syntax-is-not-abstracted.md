# 0020. 式の構文が Backend から抽象化されていない

- 状態: 受理（対処は Phase 5 着手時に行う）
- 日付: 2026-08-25
- 関連: design.md §6, §10.7, §13 / ADR 0006, ADR 0013

## 発見した事実

`IDebuggerBackend` は DAP の語彙で切ってあり、スタック・変数・ブレークポイントといった
**概念**は Backend から独立している。しかし**式の構文は独立していない。**

Visual Studio の記法が、Backend を知らないはずの層に埋まっている。

| 場所 | 内容 |
|---|---|
| `Stakeout.Core/CompositeService.cs:120` | エラーの hint に `{,,モジュール名}` |
| `Stakeout.Core/CompositeService.cs:390` | 同上 |
| `Stakeout.Core/WriteSiteDetector.cs` | `StripModuleQualifier` が `}` を探して前を捨てる |
| `Stakeout.Rpc/RequestModels.cs:74` | `Format` の説明が「VS の書式指定子」 |
| `skills/stakeout/SKILL.md` | 調査の型が `{,,DLL名}` を前提にしている |

`{,,NativeLib.dll}g_shared` は Visual Studio の構文である。DbgEng は
`NativeLib!g_shared` を使う。書式指定子（`expr,x`）も VS 固有で、DbgEng とは違う。

## 何が壊れるか

Phase 5 で DbgEng Backend を足したとき、次が起きる。

- `WriteSiteDetector.StripModuleQualifier` は `}` を探すので、`NativeLib!g_shared` から
  **何も剥がれない**。その結果 `code writers` は候補をゼロ件で返す。
  **エラーにはならず、静かに空を返す**
- エラーの hint が、その Backend では使えない構文を案内する
- Skill の「別モジュールなら `{,,モジュール名}` を付ける」が、半分の場合で誤りになる

`code writers` が黙って空を返す形の失敗は、今日すでに 1 回踏んでいる
（修飾を付けたままソースと照合して候補ゼロになった件）。想像上の心配ではない。

## 選択肢

1. **今すぐ抽象化する。** `IDebuggerBackend` に式構文の組み立てと除去を持たせる
2. **記録して、Phase 5 着手時に直す**
3. 放置する

## 決定

案 2 を採る。**今は直さない。記録だけ残す。**

理由は 3 つ。

- **実装が 1 つしか無い状態で作った抽象は、その実装の形になる。**
  いま式構文のインターフェースを切れば、それは「Visual Studio の記法を一般化したつもりのもの」
  にしかならない。DbgEng の実物を見てから決めるほうが、正しい形になる。
  **抽象が「あるのに間違っている」ほうが、「無い」より直しにくい**
- **Phase 5 に着手するまで実害がゼロ。** Backend が 1 つの間、この漏れは何も壊さない
- 忘れることのコストだけが高い。だから記録する

## Phase 5 着手時にやること

DbgEng Backend を書き始める前に、この 5 か所を式構文の抽象に寄せる。

- [ ] `IDebuggerBackend` に式構文の責務を足す（修飾の組み立て・除去・書式指定子の変換）
- [ ] `CompositeService` の 2 か所の hint を、Backend から得た構文で組み立てる
- [ ] `WriteSiteDetector.StripModuleQualifier` を Backend 経由にする。
      **ここが最も危険。** 失敗しても例外にならず、候補ゼロで返る
- [ ] `EvalOptions.Format` の意味を Backend ごとに決める
- [ ] `skills/stakeout/SKILL.md` の式の書き方を、Backend で分岐させるか、両方を併記する
- [ ] `code writers` が DbgEng セッションでも候補を返すことを統合検証に追加する

## 影響

- design.md §6 に「式の構文は Backend 依存であり、まだ抽象化されていない」と明記する
- Phase 5 の作業項目にこの ADR を参照させる
