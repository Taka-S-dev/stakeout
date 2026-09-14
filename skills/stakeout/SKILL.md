---
name: stakeout
description: Visual Studio 上で動くネイティブ C コードを調査する。「変数が壊れる」「状態がおかしい」「落ちる」といった不具合の原因箇所を、stakeout CLI を使って特定する。ユーザーが実行中のプロセスの不具合調査を依頼したとき、クラッシュやメモリ破壊の原因を探すとき、変数がいつ誰に書き換えられたかを知りたいときに使う。
---

# stakeout — ネイティブ C デバッグ

`stakeout` は Visual Studio を裏で操作する CLI。ブレークポイント・データブレークポイント・
式評価・スタック取得を、1 コマンド 1 操作で行う。

## 前提

- **常に `--json` を付ける。** 結果は `.data` に入っている
  （`{ "ok": true, "data": ... }`）。失敗は標準エラーに `{ "ok": false, "error": {...} }`
- **エラーの `hint` に従う。** 次に何をすればよいかが必ず書いてある。推測で別のコマンドを試さない
- 終了コードで分岐する
  - `0` 成功 / `1` 実行時エラー / `2` 使い方の誤り
  - `3` タイムアウト（まだ実行中）— **これは失敗ではない。もう一度 `wait` する**
  - `4` 前提条件違反（停止中でないのに読もうとした等）
  - `5` 対象なし
- `wait` に長いタイムアウトを渡さない。既定の 20 秒で足りなければ繰り返す

## 開始手順

```bash
stakeout targets --json                      # アタッチできるプロセスを見る
stakeout attach --pid <PID> --json           # または --name '^Host\.exe$'
stakeout status --json                       # 状態確認（任意）
```

Visual Studio がモーダルダイアログを出していると、stakeout は**待たずに**そう言って失敗する。
その場合はユーザーにダイアログを閉じてもらう。待っても回復しない。

## 調査の型（この順を守る）

### 0. 初めての Target なら、まず環境を確かめる

```bash
stakeout attach --pid <PID> --json && stakeout pause --json && stakeout wait --json
stakeout doctor --json
```

プロセス構成・タスクとスレッドの対応・bitness・昇格レベル・ASan の可否が出る。
`status` が `Answered` 以外の項目は、`nextStep` に従うか、ユーザーに聞く。

### 1. 再現手順とシンボルを確定する

「何が壊れるのか」を変数名か関数名で言えるまで、コマンドを打たない。
言えないなら、まずユーザーに聞く。

### 2. 検出ビルドがあれば先に使う

ASan（`/fsanitize=address`）やページヒープのビルドがあるなら、まずそれで走らせる。
止まった場所から始めれば、以下の手間はほとんど要らない。

### 3. 静的な候補を出す

```bash
stakeout code writers <SYMBOL> --json        # そのシンボルに書いている場所（Phase 4 以降）
```

### 4. 動的に犯人を特定する

**値が壊れる**なら、書き込みを捕まえる。

```bash
stakeout pause --json                        # データブレークポイントは停止中にしか張れない
stakeout wait --json
stakeout watch-until-change '{,,MyLib.dll}g_shared.counter' --timeout 60 --json
```

返るのは「値が変わった時点の書き込み元」。`taskName` とスタックが付く。
書き込み元が想定内の関数なら、それは犯人ではない。もう一度実行して次の書き込みを見る。

**特定の状態で止めたい**なら、条件付きで一気に走らせる。

```bash
stakeout run-until 'state.c:142' --cond 'g_ctx.state == 7' \
  --expr 'g_ctx.state' --expr 'g_ctx.tick' --timeout 60 --json
```

`run-until` は「一時ブレークポイントを張る → 実行 → 停止 → スタックと式を取る → 片付ける」を
1 回で行う。**この 1 コマンドで済むことを、`bp set` → `continue` → `wait` → `stack` と
分けて打たない。**

位置は、読んだソースのフルパスで書くのが確実である。ファイル名だけだと、Visual Studio が
開いている同名の別ファイルに解決されることがある。stakeout は結び付かなければ張り直しを試み、
それでも駄目なら `NOT_FOUND` を返す。**`NOT_FOUND` が返ったら、hint のとおりフルパスで打ち直す。**

### 5. 順序やタイミングの問題

```bash
stakeout trace-calls <FUNC> --expr <E> --json    # Phase 6 以降
stakeout trace query "SQL" --json
```

**EnvDTE バックエンドではトレースが毎秒 2〜6 件しか流れない。**
毎秒何万回も呼ばれる関数には使えない。低頻度の状態遷移関数だけに使う。

### 6. ここまでで決まらないときだけステップ実行

```bash
stakeout trace-expr '{,,MyLib.dll}g_ctx.state' --steps 30 --json
stakeout step over --json && stakeout locals --json
```

## 禁止事項

- **いきなり `step` で追いかけない。** 1 ステップ 33 ms かかる。100 ステップで 3 秒、
  その間 Target は実質止まっている
- **`pause` してからグローバル変数を読まない。** どのフレームで止まるか分からず、
  別モジュールのシンボルは見えない。`stakeout stack` で停止位置を確かめ、
  別モジュールなら `{,,モジュール名}式` と書く
- **ポインタを `eval` で覗いて満足しない。** `stakeout dump <expr> --depth 2` で展開する
- **C# のフレームは無視する。** `isExternal: true` が付いている
- **`stack --all` を深く取らない。** 1 フレーム 4 ms。既定の深さ 5 で足りることが多い
- **データブレークポイントに `--cond` を付けない。** 付けられない（`UNSUPPORTED`）。
  値で絞るなら書き込む行に `run-until --cond`、誰が書いたかを知りたいなら `watch-until-change` を使う

## 式の書き方

- 式は**停止しているフレームのスコープ**で解決される
- 別モジュールのグローバル: `{,,NativeLib.dll}g_ctx.state`
- 書式指定: `stakeout eval 'g_ctx.name' --format s --json`（`x` 16進 / `d` 10進 / `s` 文字列）
- 代入もできる: `stakeout eval '{,,NativeLib.dll}g_flag = 1' --json`

## 生のメモリを見る

型が付いた読み方（`eval` / `dump`）で足りるなら、そちらを使う。
`mem` が要るのは**型が信用できないとき**である。構造体が壊れている、
ヒープのヘッダを見たい、アライメントを確かめたい、といった場合。

```bash
stakeout mem '&g_ctx' -n 64 --json          # 式でもアドレスでもよい
stakeout mem '0x7ff6a2c31040' -n 16 --json
```

- **`length` が `requested` より小さければ、そこから先は読めていない。**
  0 で埋めた結果ではない。未マップの領域とゼロ埋めされた領域を取り違えないこと
- EnvDTE では式評価で代替しているので**遅い**。既定の上限は 4 KiB。
  範囲を広げる前に、まず `dump` で足りないかを考える

## スレッドとタスク

```bash
stakeout task-map --json                     # スレッドとタスク名の対応
stakeout thread select --task C --json       # タスク名で選ぶ
stakeout thread freeze --task A --json       # 邪魔なタスクを止める
```

スレッド ID は停止のたびに意味を失う。**タスク名で指定する。**

## 大きい結果

応答が上限を超えると `truncated: true` と `cursor` が返る。

```bash
stakeout dump 'g_ctx' --depth 3 --json                    # 1 ページ目
stakeout dump 'g_ctx' --cursor <CURSOR> --json            # 続き
```

カーソルは 10 分で失効し、**1 度しか使えない**。
続きが多いなら、`--depth` や `--max-items` を下げて取り直すほうが速い。

## 後始末

```bash
stakeout bp clear --json                     # 張ったブレークポイントを消す
stakeout detach --json                       # Target は生きたまま残る
```

データブレークポイントは **4 本しかない**。使い終わったら必ず消す。

## 報告の型

調査が終わったら、この 4 つを書く。

1. **再現手順** — 何をすると起きるか
2. **原因** — `file:line` と、そこで何が起きているか
3. **根拠** — どのコマンドの、どの出力から言えるのか（`steps` と停止位置を引用する）
4. **未確認** — 確かめていないこと、別の可能性

**根拠のない断定をしない。** 「おそらく」で終わるなら、何を確かめれば白黒つくかを書く。
