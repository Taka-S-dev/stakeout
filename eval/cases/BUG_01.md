# BUG_01 — 構造体の中身が壊れる

## 症状（ユーザーが最初に言いそうな文）

名前を設定したあと、同じ構造体の別のメンバが読めない値になっている。
名前は正しく入っているように見えるのに、その後ろのフィールドが化けている。

## 前提

- ビルド: `samples/target/build.ps1 -Bug BUG_01`
- 起動: `Harness.exe eval --slow --overflow`
- 調査対象のグローバル: `g_ctx`（NativeLib.dll）。`inner.flags` は `0xA5A5A5A5` のはず

## 正解

- ファイル: `samples/target/NativeLib/nativelib.c`
- 関数: `nl_set_name`
- 状態: `strcpy` で長さを検査せずに `g_ctx.name`（32 バイト）へ書き、
  後続の `g_ctx.inner` を踏み潰している

## 判定

出力に `nl_set_name` と `strcpy`（またはバッファオーバーラン）が含まれていれば正解とする。

## 想定される最短経路

1. `stakeout attach` → `stakeout pause` → `stakeout wait`
2. `stakeout dump '{,,NativeLib.dll}g_ctx' --depth 2` で `inner.flags` の異常を確認
3. `name` の直後に `inner` があることと、`nl_set_name` の実装から特定
