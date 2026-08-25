# 0001. 開発機の環境と設計前提の差分

- 状態: 受理
- 日付: 2026-08-25
- 関連: design.md §3.1, §3.2, §11.2

## 背景

design.md §3.1 は「IDE: Visual Studio 2019（ProgID `VisualStudio.DTE.16.0`）」「実装言語 C# / .NET 8」を
確定前提としている。実装を始めるにあたり開発機の実際の構成を調べた。

## 発見した事実

| 項目 | 設計の前提 | 開発機の実際 |
|---|---|---|
| .NET SDK | .NET 8 | SDK は 10.0.201 のみ。ただし `net8.0-windows` はビルド可、ランタイム 8.0.25 あり |
| Visual Studio | 2019（DTE 16.0） | **未インストール**。VS 2026 Community（DTE 18.0）のみ。ProgID は 17.0 / 18.0 が登録済み |
| MSVC ツールセット | （暗黙に必要） | あり（14.44 / 14.50） |
| dbgeng.dll / cdb.exe | Debugging Tools\x64 にある想定（§11.2） | **無い**。Windows SDK 同梱の dbghelp / symsrv / dbgcore のみ |
| gtags / global | 利用可（§3.1） | あり |

調査対象の実 Target が動くのは VS2019 の環境であり、この開発機は開発・検証用の別環境である。

## 決定

1. ターゲットフレームワークは design.md 通り `net8.0-windows` を維持する。SDK 10 でビルドでき、
   実運用の機械に .NET 8 ランタイムしか無い場合にも動く。SDK 10 前提にする利点が無い。
2. EnvDTE バックエンドの検証は VS 2026（DTE 18.0）で行う。ROT 経由の取得も `EnvDTE.Debugger`
   の API 面も 16.0 と 18.0 で互換であり、検証の価値は保たれる。版数依存は ADR 0002 で吸収する。
3. Phase 6（DbgEng）は Debugging Tools for Windows の導入が前提条件であることを明示し、
   導入されるまで着手しない。design.md §20 のフェーズ順は変えない。

## 影響

- design.md §3.1 の「IDE: Visual Studio 2019」は「Visual Studio 2019 以降」に読み替える
- design.md §16 の設定キー `envdte.progId` は既定値を持たせず、未指定なら自動検出とする（ADR 0002）
- Phase 6 の完了条件に「Debugging Tools for Windows がインストールされていること」を追加する
