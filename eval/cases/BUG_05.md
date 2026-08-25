# BUG_05 — アクセス違反で落ちる

## 症状（ユーザーが最初に言いそうな文）

Harness.exe が設定を読むところで落ちる。
アクセス違反らしいが、どこで何を触って落ちているのか分からない。

## 前提

- ビルド: `samples/target/build.ps1 -Bug BUG_05`
- 起動: `Harness.exe eval --slow`
- クラッシュは `{,,NativeLib.dll}g_crash_requested = 1` を書き込むと起きる
  （デバッガの式評価から起こせる）

## 正解

- ファイル: `samples/target/NativeLib/nativelib.c`
- 関数: `nl_read_config` がヌルポインタを `nl_deref` に渡している
- 状態: `config` が NULL のまま逆参照される

## 判定

出力に `nativelib.c` と、`nl_read_config` または `nl_deref` が含まれ、
「NULL ポインタの逆参照」旨が書かれていれば正解とする。

## 想定される最短経路

1. `stakeout attach`
2. `stakeout bp exceptions --on --codes C0000005`
3. `stakeout pause` → `stakeout eval '{,,NativeLib.dll}g_crash_requested = 1'` → `stakeout continue`
4. `stakeout wait` で例外停止を捉え、`stakeout stack` で発生元を特定
