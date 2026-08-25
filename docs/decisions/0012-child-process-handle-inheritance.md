# 0012. 常駐デーモンは ShellExecute で起動する

- 状態: 受理
- 日付: 2026-08-25
- 関連: design.md §8.1

## 発見した事実

`stakeout` はデーモンが居なければ `stakeout --detach` を起動する（design.md §8.1）。
これを `Process.Start` の既定（`UseShellExecute = false`）で行うと、
**`stakeout` の標準出力ハンドルが常駐デーモンに引き継がれる**。

その結果、`stakeout` 自身が終了しても出力パイプが閉じない。`stakeout` の出力を
読んでいる側は永久に待つ。実際に次の 3 つで再現した。

- シェルのパイプ（`stakeout status | head`）
- PowerShell の出力キャプチャ（`$out = & stakeout status`）
- 統合検証スクリプト全体（最初のコマンドで固まる）

**標準ストリームをリダイレクトしても解決しない。** リダイレクトは子に
新しいパイプを与えるだけで、継承そのものは止まらない。むしろ .NET は
リダイレクト時に `CreateProcess` を `bInheritHandles = TRUE` で呼ぶ。

同じ理由で、統合検証が `Start-Process` で起動する Harness も、
出力をファイルに逃がさないと同じ現象を起こす。

## 選択肢

1. 標準ストリームをリダイレクトする → 効果なし（上記）
2. `UseShellExecute = true` にする → ShellExecuteEx はハンドルを継承しない
3. `CreateProcess` を P/Invoke して `bInheritHandles = FALSE` で呼ぶ

案 3 は完全に制御できるが、この 1 箇所のために Win32 のプロセス生成を
自前で持つのは重い。案 2 で目的は達せられる。

## 決定

案 2 を採る。`UseShellExecute = true` + `WindowStyle = Hidden` で起動する。

代償として、デーモンの標準エラーを読めなくなる。起動に失敗した理由
（設定ファイルが壊れている等）は、**デーモンが
`%LOCALAPPDATA%\stakeout\startup-error.txt` に書き残し、`stakeout` がそれを読む**。

- デーモンは起動のたびにこのファイルを消してから始める。
  消さないと、今回成功したのに前回の理由が報告される
- `stakeout` は「起動したのに繋がらない」ときだけこのファイルを読む

この経路が無いと、設定ミスが「stakeoutd.exe が見つかりません」という
まったく見当違いのエラーになり、原因に辿り着けない。

## 影響

- `DaemonClient.TryStartDaemon` を ShellExecute に変更した
- `StakeoutPaths.StartupErrorFile` / `RpcTransport.StartupErrorFile` を追加した
- `tests/integration/phase1.ps1` は Harness の出力をファイルにリダイレクトする
- 同種の問題を避けるため、**stakeout から子プロセスを起こす箇所を今後増やさない**
