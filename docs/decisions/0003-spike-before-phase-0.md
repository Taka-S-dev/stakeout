# 0003. Phase 0 の前に検証スパイクを置く

- 状態: 受理
- 日付: 2026-08-25
- 関連: design.md §0.6, §10.5, §10.6, §20

## 背景

design.md には `[要検証]` が 6 箇所ある。このうち次の 3 つは、結果次第で
アーキテクチャの主従（EnvDTE を主にするか DbgEng を主にするか）が変わる。

- §10.5 データブレークポイント `Breakpoints.Add(Data:, DataCount:)` が実際に張れて止まるか
- §10.6 Tracepoint（`BreakWhenHit=false` + `Message`）が式を評価し、出力ペインから回収できるか、
  その実効スループットはどれだけか
- §10.2 `DebuggerEvents.OnEnterBreakMode` が取りこぼしなく飛ぶか

design.md §20 の順序に素直に従うと、これらが判明するのは Phase 1 の終盤〜Phase 4 になる。
そこで「EnvDTE では無理」と分かった場合、Phase 1〜4 の実装のかなりの部分が無駄になる。

## 決定

Phase 0 の前に **Phase -1（スパイク）** を置く。

- 置き場所は `spike/`。リポジトリ構成（design.md §19）に含めず、成果物はコードではなく ADR とする
- 使い捨てを明示する。テストを書かない、抽象化しない、`Stakeout.Core` に依存させない
- スパイクの結論が出たら `spike/` のコードは残すが、以後参照しない（Phase 1 は白紙から書く）

検証項目（各項目が ADR 1 本になる）:

| # | 項目 | 判定基準 |
|---|---|---|
| S1 | ROT 走査 → DTE 取得 → メッセージフィルタ登録 | VS がビジー（ビルド中）でも呼び出しが成功する |
| S2 | Attach2("Native") → line BP → break → GetExpression2 | C の構造体メンバが読める |
| S3 | データ BP `Breakpoints.Add(Data:, DataCount:)` | 実際に張れて、書き込みで止まる |
| S4 | Tracepoint + 出力ペイン回収 | 式が評価される。1000 ヒットの所要時間を計測する |
| S5 | `OnEnterBreakMode` の配送信頼性 | break/go を 100 回繰り返して取りこぼし 0 |

S3 と S4 が両方とも不可だった場合、design.md §20 のフェーズ順を組み替え、
DbgEng を Phase 1 に繰り上げる ADR を別途起こす。Backend IF と `BackendCapabilities` が
既にあるため、設計そのものの変更は不要である。

## 影響

- docs/phase-log.md の現在フェーズは「Phase -1」から始まる
- スパイクには検証対象となるネイティブ Target が要る。design.md §18.1 のサンプルのうち、
  S1〜S5 に必要な最小限（C DLL + C ハーネス exe、BUG_03 相当のグローバル書き込み）だけを先に作る
