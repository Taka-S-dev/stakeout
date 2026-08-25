# BUG_03 — 共有カウンタが飛ぶ

## 症状（ユーザーが最初に言いそうな文）

複数のタスクから触っている共有カウンタが、ときどきまったく違う値になる。
1 ずつ増えるはずなのに、いきなり大きな値に変わっていることがある。
誰が書いているのか知りたい。

## 前提

- ビルド: `samples/target/build.ps1 -Bug BUG_03`
- 起動: `Harness.exe eval --slow`
- 調査対象のグローバル: `g_shared.counter`（NativeLib.dll）

## 正解

- ファイル: `samples/target/NativeLib/nativelib.c`
- 関数: `nl_stray_write`（`Task_C_Main` から呼ばれる）
- 状態: `g_shared.scratch[4]` への範囲外書き込み。scratch は 4 要素なので、
  構造体上その直後にある `counter` を踏み潰している

ソースを `counter` で検索しても、この書き込みは見つからない。
字面に `counter` が現れないためである。

## 判定

出力に `nl_stray_write` と、タスク C（または `Task_C_Main`）が含まれていれば正解とする。

## 想定される最短経路

1. `stakeout code writers g_shared.counter` で静的な候補を出す（`nl_bump_counter` は候補に入る）
2. `stakeout attach` → `stakeout pause` → `stakeout wait`
3. `stakeout find-corruption '{,,NativeLib.dll}g_shared.counter'`
4. 候補に無い書き込みとして `nl_stray_write` が名指しされる
