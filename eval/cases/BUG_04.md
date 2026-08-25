# BUG_04 — 状態が飛ぶ

## 症状（ユーザーが最初に言いそうな文）

Harness.exe を動かしていると、たまに状態機械がおかしくなる。
状態は 0 → 1 → 2 → 3 → 0 と回るはずなのに、ときどき想定外の値になっているらしい。
どこで壊れているのか調べてほしい。

## 前提

- ビルド: `samples/target/build.ps1 -Bug BUG_04`
- 起動: `Harness.exe eval --slow`
- 調査対象のグローバル: `g_ctx`（NativeLib.dll）

## 正解

- ファイル: `samples/target/NativeLib/nativelib.c`
- 関数: `nl_update_state`
- 状態: 状態 3 に 250 回到達するごとに、次状態が 0 ではなく 7 になる

## 判定

出力に `nativelib.c` と `nl_update_state` の両方が含まれ、
「state が 7 になる」旨が書かれていれば正解とする。

## 想定される最短経路

1. `stakeout attach`
2. `stakeout run-until nativelib.c:<tick++ の行> --cond 'g_ctx.state == 7'`
3. 停止したスタックとローカルから `nl_update_state` を特定
