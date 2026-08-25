# 0006. データブレークポイントは使える。ただし式の解決と戻り値に罠がある

- 状態: 受理
- 日付: 2026-08-25
- 検証: S3（spike/results/spike-final.json, spike-02.json）
- 関連: design.md §9.2, §9.5, §10.5, §20 Phase 2 / Phase 5

## 結論

**EnvDTE 経由でデータブレークポイントは張れる。実際に書き込みで停止する。**
design.md §10.5 の `[要検証]` は肯定的に解決した。`watch-until-change` と
`find-corruption` を EnvDTE Backend で実装してよい。

検証では `nl_stray_write`（想定外の書き込み）と `nl_bump_counter`（想定内の書き込み）の
両方で停止し、停止スレッド ID と関数名を取得できた。これは `find-corruption` が
必要とする情報そのものである。

ただし、そのまま実装すると必ず踏む罠が 3 つある。

## 罠 1 — データ式は現在のフレームのスコープで解決される

`Breakpoints.Add(Data: "&g_shared.counter", DataCount: 4)` は、
**停止しているフレームによって成功したり失敗したりする。**

| 停止位置 | `&g_shared.counter` | `{,,NativeLib.dll}&g_shared.counter` |
|---|---|---|
| `main`（Harness.exe のフレーム） | `0x89711010`「データ式が新しいブレークポイントに対して無効です」 | 成功 |
| `nl_update_state`（NativeLib.dll のフレーム） | 成功 | （後述の罠 2 により判定不能） |

`g_shared` は NativeLib.dll のグローバルなので、Harness.exe のフレームからは名前解決できない。
モジュール修飾のコンテキスト演算子 `{,,モジュール名}` を付ければ、どのフレームからでも解決できる。

## 罠 2 — `Breakpoints.Add` は作成しても空のコレクションを返すことがある

検証で、`{,,NativeLib.dll}&g_shared.counter` と `&g_shared.counter` は
**例外を投げず、`Count == 0` のコレクションを返した**。にもかかわらず、
実際にはブレークポイントが作成されていた（後で `BreakpointLastHit` が
正しいアドレス `0x00007FF92CBC8268` を報告した）。

その結果、「まだ作れていない」と誤認した検証コードが次の候補も試し、
**意図しないデータブレークポイントを 3 本作ってしまった**。x64 のハードウェアデータ BP は
4 本しかないため、これは実運用で致命的になる。

## 罠 3 — 誤ったデータ式が、黙って別のアドレスを監視する

`Data: "g_shared.counter"`（`&` を付け忘れた形）は例外を投げずに成功し、
作成されたブレークポイントの名前は
`'0x000000000BADF027' から (4 バイト)変わった場合` だった。

`0x0BADF00D` は `nl_stray_write` が書き込む**値**である。つまり VS は
「counter の値」をアドレスとして解釈し、まったく無関係な番地を監視するブレークポイントを作った。
エラーは出ない。調査者はそれに気づかないまま「書き込みが検出されない」と誤解する。

## 決定

1. データ式は必ず**モジュール修飾 + アドレス取得**の形で組み立てる:
   `{,,<module>}&<expr>`。モジュール名は事前に `vars.eval` でシンボルが属するモジュールを
   引いて決める（`&expr` の評価結果に `{NativeLib.dll!Shared g_shared}` の形で含まれる）。
2. `Breakpoints.Add` の戻り値を信用しない。**追加の前後で `Debugger.Breakpoints` を
   スナップショットし、その差分を「実際に作成されたもの」とする。**
   この差分ロジックは Backend の共通ヘルパにする（行 BP・関数 BP でも同じ問題がありうる）。
3. 作成されたデータ BP の `Name` からアドレスを抜き出し、
   **意図したアドレス（`&expr` の評価結果）と一致するか検証する。**
   一致しなければ即座に削除し、`BACKEND` エラーで「データ式が別のアドレスに解決された」と返す。
   罠 3 を黙って通さない。
4. ハードウェアデータ BP は 4 本しかない。stakeout は自分が張ったデータ BP を数え、
   4 本を超える要求は `UNSUPPORTED` + hint（「既存のデータ BP を削除してください」）で拒否する。
   `watch-until-change` / `find-corruption` は必ず自分の BP を後始末する。
5. データ BP は中断中にしか張れない。`watch-until-change` は前提条件
   `Attached_Stopped` を維持する（design.md §9.2 の通り）。

## 影響

- design.md §10.5 の Data BP 行から `[要検証]` を外し、上記の制約を追記する
- design.md §9.2 / §9.5 に「データ式の組み立て」と「アドレス検証」の手順を追加する
- Backend に `SnapshotBreakpoints()` / `DiffBreakpoints()` ヘルパを設ける（Phase 1）
- `BackendCapabilities.DataBreakpoint` は EnvDTE でも `true` にできる
- 補足: データ BP を張ったままデタッチしても Target は生き残ることを確認した（design.md §15.3 の要件を満たす）
