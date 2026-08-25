# 0002. DTE の ProgID をバージョン非依存で解決する

- 状態: 受理
- 日付: 2026-08-25
- 関連: design.md §10.1, §16 / ADR 0001

## 背景

design.md §10.1 は ROT の表示名を `!VisualStudio.DTE.16.0:<pid>` にマッチさせると書いており、
§16 の設定例も `"progId": "VisualStudio.DTE.16.0"` を既定値としている。

しかし開発機には VS2019 が無く（ADR 0001）、また実運用でも VS の版が上がるたびに設定を
書き換えるのは、エージェントに使わせる道具として不必要な失敗要因になる。VS が複数バージョン
同居している環境も珍しくない。

## 選択肢

1. ProgID を設定で固定する（設計書の現状）
2. ROT を `VisualStudio.DTE.<major>.<minor>:<pid>` の正規表現で走査し、見つかった全 VS を候補にする
3. vswhere.exe を呼んでインストール済み VS を列挙し、その版数の ProgID だけを試す

案 3 は vswhere の存在に依存し、かつ「起動中の VS」を直接は教えてくれない（インストール済みと
起動中は別）。ROT は「今起動している VS」そのものの一覧なので、目的に対して案 2 が直接的。

## 決定

案 2 を採る。

- ROT の表示名を正規表現 `^!VisualStudio\.DTE\.(\d+)\.(\d+):(\d+)$` で走査する
- 得られた候補を `{ProgId, Version, Pid}` のリストとして保持する
- 選択規則（design.md §10.1 の意図を保ちつつ拡張）:
  1. 設定 `envdte.vsPid` があればその pid の VS
  2. 設定 `envdte.progId` があればその ProgID に一致するもの（明示的な版固定は引き続き可能）
  3. Target をデバッグ中の VS（`Debugger.DebuggedProcesses` に Target が含まれるもの）
  4. 候補が 1 つならそれ
  5. 複数残ったら **エラーにする**（`NOT_FOUND` ではなく `PRECONDITION`）。hint に候補の
     `{version, pid, solution}` を列挙し、`--vs-pid` を指定するよう促す
- 設定 `envdte.progId` の既定値は `null`（自動検出）にする

案 4 の「最初の 1 つを warning 付きで選ぶ」（設計書の現状）は採らない。
間違った VS に繋いだまま調査が進むと、エージェントが原因不明の矛盾した結果を延々と追うことになり、
警告よりも失敗させたほうが安い。

## 影響

- design.md §10.1 の「無ければ最初の 1 つ（warning）」を「複数残ったらエラー」に改める
- design.md §16 の設定例から `"progId": "VisualStudio.DTE.16.0"` の既定値を外す
- CLI に `stakeout attach --vs-pid N` を追加する（Phase 1）
- `daemon.status` に検出した VS 候補一覧を含める（Phase 0 では ROT 走査のみ実装可能）
