# 0007. EnvDTE のトレースポイントは機能するが遅すぎる。Phase 4 を組み替える

- 状態: 受理
- 日付: 2026-08-25
- 検証: S4（spike/results/spike-final.json, spike-02.json）
- 関連: design.md §9.4, §10.6, §12, §20 Phase 4

## 発見した事実

### 機能面は設計どおり動く

- `Breakpoint2.BreakWhenHit = false` と `Breakpoint2.Message` は EnvDTE から設定できる
- Message 内の `{式}` は**実際に評価される**。`$TID` / `$FUNCTION` も展開される
- 出力は出力ウィンドウのデバッグペインに出る。`TextDocument` から読み取れる

実際に得られた行:

```
STAKEOUT|3|1484096|87076|nl_update_state(int)
```

design.md §10.6 の回収方式（固定書式のマーカー付き行を出力ペインから拾う）は成立する。

### 出力ペイン名は VS の表示言語で変わる

検証機（日本語版 VS）のペイン名は次のとおりだった。

```
ソース管理 - Team Foundation / ビルド / ビルドの順序 / データベース出力 /
テスト / ソース管理 / デバッグ / GitHub Copilot / ホット リロード
```

design.md §10.6 の `OutputWindowPanes.Item("Debug")` は**日本語環境で失敗する**。
ペインは名前ではなく中身（マーカー行の有無）で特定する必要がある。

### スループットが実用に耐えない

| 計測 | 結果 |
|---|---|
| 10 秒間 | 62 ヒット（6.2 hits/s） |
| 15 秒間 | 38 ヒット（2.5 hits/s） |

Target 側の `nl_update_state` は本来**毎秒 27 万回**呼ばれている
（デバッガ非接続時の実測）。トレースポイントを 1 本張っただけで、
実行速度が **10 万分の 1 程度**まで落ちる。

design.md §20 Phase 4 の完了条件
「`trace-calls update_state --max-hits 500` が run を作る」は、
この速度では **200 秒以上**かかる。60 秒のタイムアウト（§8.6）にも収まらない。

なお auto-continue フォールバック（通常 BP で止めて式評価して `Go`）も救いにならない。
S5 の計測では break/go の 1 サイクルが **33.4 ms** であり、30 hits/s が上限。
トレースポイントより速いが、桁は変わらない。

## 判断

**EnvDTE Backend の上に Trace DB を作るのは、投資対効果が合わない。**

トレースポイントの価値は「止めずに大量のイベントを集める」ことにある。
毎秒数件しか集まらないなら、`run-until` で止めて `dump` するのと情報量が変わらず、
むしろ Target の実時間挙動を壊す分だけ悪い。

一方、**低頻度の関数には有効**である。毎秒数回しか呼ばれない状態遷移関数や
エラーハンドラなら、2.5 hits/s でも十分に追える。

## 決定

1. design.md §20 のフェーズ順を組み替える。

   | 変更前 | 変更後 |
   |---|---|
   | Phase 4: Trace DB と Tracepoint | **Phase 4: Code Index と find-corruption**（旧 Phase 5） |
   | Phase 5: Code Index と find-corruption | **Phase 5: DbgEng Backend**（旧 Phase 6） |
   | Phase 6: DbgEng Backend | **Phase 6: Trace DB と Tracepoint**（旧 Phase 4、DbgEng 前提） |

   理由: Trace DB は「速いトレース源」があって初めて価値が出る。
   DbgEng の `IDebugEventCallbacks`（design.md §11.4）はプロセス内で数 ms とされており、
   そちらを先に作ってから Trace DB を載せるほうが、作り直しが無い。
   Code Index（旧 Phase 5）はデバッガに一切依存せず単独で価値が出るので、前に出す。

2. `trace-calls` は Phase 6 まで実装しない。ただし **`BackendCapabilities` に
   `TracepointHitsPerSecond`（実測値）を持たせ**、Composite 層が
   「この Backend でこのヒット数を集めるには何秒かかるか」を見積もって、
   非現実的なら実行前に `UNSUPPORTED` + hint で断れるようにする。

   ```
   この Backend のトレース速度は約 2.5 hits/s です。500 ヒットの収集には
   約 200 秒かかり、タイムアウト (60s) を超えます。
   --max-hits を 100 以下にするか、--backend dbgeng を使ってください。
   ```

3. 出力ペインは**名前で引かない**。`OutputWindowPanes` を列挙し、
   マーカー行 `STAKEOUT|` を含むペインを内容で特定してキャッシュする。

4. Skill（design.md §17）の「調査の型」から、トレース起点の手順を
   EnvDTE 環境では推奨しないよう書き分ける。EnvDTE では
   `code writers` → `find-corruption` → `run-until` + `dump` を主線にする。

## 影響

- design.md §20 のフェーズ表を差し替える
- design.md §10.6 の「`Item("Debug")`」を内容ベースの特定に改める
- design.md §10.8 の Capabilities に `TracepointHitsPerSecond` を追加する
- Phase 6（新）の完了条件に、DbgEng で 500 hits を 60 秒以内に集められること、を入れる
- 未解決: 実 Target（C# ホスト + C DLL）でも同じ速度かは未確認。
  ただし遅さの原因は VS の出力ウィンドウ側にあると考えられ、改善は期待しにくい
