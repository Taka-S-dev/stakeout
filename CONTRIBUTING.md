# 開発の進め方

設計は [docs/design.md](docs/design.md) が正。

## 作業ルール

- design.md §20 のフェーズ順に実装する。現在のフェーズは
  [docs/phase-log.md](docs/phase-log.md) の末尾を見る
- 次フェーズの機能を先取りしない
- 設計と矛盾する事実が出たら [docs/decisions/](docs/decisions/) に ADR を書いて止まる
- `[要検証]` 項目は実装時に必ず検証し、結果を ADR に残す

## ビルド・テスト

- `dotnet build` でビルド、`tests/run.ps1` でテスト（Backend なしで通る）
- `dotnet test` は使えない。xunit v3 のテストを検出できない（ADR 0010）
- 統合テストは samples/target を Visual Studio で開いてから `tests/integration/*.ps1`。
  **1 つずつ走らせる。** 前のデーモンと Target が終わる前に次を始めると
  アタッチが競合して落ちる
- サンプル C ターゲットのビルドは `samples/target/build.ps1`（MSVC が要る）

## 触ってはいけないもの

- `samples/target` の仕込みバグ（`#ifdef BUG_NN`）は修正しない。テストの入力である
- [docs/decisions/](docs/decisions/) の既存 ADR は書き換えない。覆すときは新しい ADR を足す

## コミット

Conventional Commits に従う。件名は英語・命令形・小文字始まり・ピリオドなし。
本文は利用者から見た変化を箇条書きにし、理由をその中に畳む。

## 環境の注意

- 開発機には Visual Studio 2019 は無い（ADR 0001）。DTE は版数非依存で解決する（ADR 0002）
- 開発機には dbgeng.dll / cdb.exe が無い。Phase 6 は Debugging Tools for Windows の導入が前提
