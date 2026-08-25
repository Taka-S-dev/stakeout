# tests/integration/

実際の Visual Studio と `samples/target` を使う検証。**CI では動かない。手で走らせる。**

## 前提

1. `dotnet build`
2. `samples/target/build.ps1 -Bug <BUG_01|BUG_03|BUG_04|BUG_05>`（必要なものだけ）
3. `samples/target` で `gtags`（phase4 のみ）
4. Visual Studio を起動し、**スタートウィンドウを抜けて、モーダルダイアログが無い状態**にする
   - この状態でないと DTE は一切応答しない（ADR 0004）

## 実行

```powershell
pwsh tests/integration/phase1.ps1 -VsPid <devenv の pid>              # 既定は BUG_04
pwsh tests/integration/phase1.ps1 -VsPid <pid> -Bug BUG_05           # 例外ブレーク
pwsh tests/integration/phase2.ps1 -VsPid <pid>
pwsh tests/integration/phase4.ps1 -VsPid <pid>
pwsh tests/integration/phase7.ps1 -VsPid <pid>
```

**1 本ずつ走らせること。** 続けて回すときは、前の実行のデーモンと Harness が
終わってから次を始める。落ち切る前に次を起動すると、アタッチが競合して
本来通る検証が落ちる（実際に 1 度そうなった）。

`devenv` の pid は `Get-Process devenv` で分かる。

## 書くときの注意

ここで何度も踏んだ落とし穴。

- **子プロセスの標準出力を必ずファイルに逃がす。** `Start-Process` の既定では
  Harness がスクリプトの標準出力ハンドルを引き継ぎ、出力を読む側が固まる（ADR 0012）
- **`$ErrorActionPreference = 'Stop'` のまま `2>&1` しない。** stakeout が標準エラーへ書いた
  時点で終了エラーになり、想定内の失敗を検証できない
- **`[Console]::OutputEncoding` を UTF-8 にする。** 既定（CP932）では日本語が化ける
- **配列は代入時に `@()` で包む。** 関数から `@()` を返しても PowerShell が展開する
- **行番号を決め打ちしない。** `Select-String` でソースから引く
