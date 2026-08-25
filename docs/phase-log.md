# フェーズ進捗ログ

新しい記録を末尾に追記する。各エントリは「日付 / フェーズ / やったこと / 検証結果 / 次」。

---

## 2026-08-25 — Phase -1（スパイク）着手

やったこと:

- リポジトリ骨組みを作成（docs / src / tests / samples / spike / skills / eval）
- 開発機の環境を調査し、設計の前提との差分を ADR 0001 に記録
- ADR 0002（DTE の版数非依存解決）、ADR 0003（スパイク先行）を起票

検証結果:

- `net8.0-windows` は .NET SDK 10 でビルドできる。ランタイム 8.0.25 も存在する
- Visual Studio 2019 は開発機に無い。VS 2026（DTE 18.0）で代替検証する
- dbgeng.dll / cdb.exe は無い。Phase 6 は未着手のまま据え置き

次: spike/DteProbe で design.md の `[要検証]` 5 項目を潰す（ADR 0003）。

---

## 2026-08-25 — Phase -1（スパイク）完了

やったこと:

- `samples/target` の最小版を作成（NativeLib.dll + Harness.exe、MSVC でビルド）
- `spike/DteProbe` を作成し、ADR 0003 の S1〜S5 をすべて実測
- 結果を `spike/results/spike-final.json` に保存し、ADR 0004〜0008 に記録

検証結果（S1〜S5 すべて成功）:

| # | 項目 | 結果 |
|---|---|---|
| S1 | ROT / DTE / メッセージフィルタ | OK。ただしモーダルダイアログが DTE を無期限にブロックする（ADR 0004） |
| S2 | アタッチ / 行 BP / 式評価 | OK。ただし `Attach2` は失敗し `Attach()` のみ成功（ADR 0005） |
| S3 | データブレークポイント | **使える**。式の解決と戻り値に罠あり（ADR 0006） |
| S4 | トレースポイント | 機能するが **2.5〜6 hits/s** で実用外（ADR 0007） |
| S5 | イベント配送 | 20/20 で取りこぼし 0。参照保持が条件（ADR 0008） |

主要な実測値: COM 呼び出し 0.048 ms / 式評価 6.3 ms / スタック 1 段 4.0 ms /
break-go 1 サイクル 33.4 ms。

設計への影響（ADR 0007）: **フェーズ順を組み替える。**
Trace DB（旧 Phase 4）を DbgEng の後ろに下げ、Code Index と find-corruption を前に出す。

次: ユーザーの確認を得てから Phase 0（骨組み）に着手する。
ADR 0004〜0008 の決定を design.md に反映するかどうかは、ユーザーの判断を待つ。

---

## 2026-08-25 — Phase 0（骨組み）完了

やったこと:

- ADR 0004〜0008 の決定を docs/design.md に反映（フェーズ順の組み替えを含む）
- ソリューション構成を作成（Stakeout.Rpc / Stakeout.Core / Stakeout.Daemon / Stakeout.Cli + テスト 2 本）
- `stakeoutd.exe`: 名前付きパイプ + JSON-RPC 2.0、`daemon.ping` / `daemon.status` / `daemon.shutdown`
- `stakeout.exe`: `status`、`daemon start|stop|restart|status`、自動起動、`--json`、終了コード
- 設定読み込み（探索順つき深いマージ）、JSONL セッションログ、ページングとカーソル、`NullBackend`
- 単体テスト 52 件（Core 31 / Cli 21）

完了条件の確認:

- `stakeout status --json` がデーモンを自動起動して状態を返す（exit 0）
- `stakeout daemon stop` で終了し、ログに `rpc.request` / `rpc.response` が残る
- 単体テスト: 設定の探索順・深いマージ・ページング・カーソル期限・終了コードの対応

実装中に見つけて直したもの:

- 自動起動したデーモンが親の標準出力ハンドルを握ったままになり、`stakeout` を
  パイプで受けている側が永久に待つ（子プロセスのストリームを切り離して解決）
- `daemon.shutdown` の応答とログが、停止が先に走って失われる（250 ms の猶予を入れて解決）
- `stakeout daemon stop` が成功時にも「動いていません」と表示する（先に ping して生存を確かめる）

新しい ADR:

- ADR 0009: FluentAssertions を使わない（8.x から有償ライセンス）
- ADR 0010: `dotnet test` が xunit v3 を検出できないため `tests/run.ps1` で走らせる

次: Phase 1（EnvDTE Backend）。ADR 0002・0004〜0006・0008 の決定をそのまま実装に落とす。

---

## 2026-08-25 — Phase 1（EnvDTE Backend）完了

やったこと:

- `Stakeout.Backend.EnvDte` を作成（ROT 走査・メッセージフィルタ・呼び出し側リトライ・
  Win32 による準備完了判定・専用 STA スレッド・ブレークポイント差分検証）
- `SessionGroup`（design.md §6 の薄い実装）と allowlist、`session.*` / `exec.*` /
  `threads.*` / `stack.*` / `vars.*` / `bp.*` の RPC
- CLI に `targets` / `attach` / `detach` / `continue` / `pause` / `step` / `wait` /
  `threads` / `thread` / `stack` / `locals` / `eval` / `expand` / `bp` を追加
- `samples/target` に BUG_01 / BUG_04 / BUG_05 を実装（`build.ps1 -Bug` で切り替え）
- `tests/integration/phase1.ps1`（実際の Visual Studio と Target を使う手動検証）
- 単体テスト 72 件（Core 31 / Cli 21 / Backend 20）

完了条件の確認（`-Bug BUG_04` で 15 項目、`-Bug BUG_05` で 16 項目、いずれも fail=0）:

- Native のみでアタッチ（`Attach2` は失敗し `Attach()` にフォールバック。ADR 0005）
- 条件付きブレークポイントで BUG_04 の `state==7` を捉え、`stack --all` に 6 スレッド
- `g_ctx` の構造体メンバを読み、`expand` で子要素を列挙
- `bp exceptions --on --codes C0000005` で BUG_05 のアクセス違反を `nl_deref` で捕捉
- デタッチ後も Target が生存
- 待機中の `status` が 5 秒以内に返る／読み取りの `PRECONDITION`／`pause`・`continue` の冪等性

実装中に見つけて直したもの:

- 停止待ちが単一 STA スレッドを占有し、待機中は `status` が 13.9 秒返らなかった（ADR 0011）
- 自動起動したデーモンが `stakeout` の標準出力ハンドルを継承し、出力を読む側が固まった。
  リダイレクトでは直らず、ShellExecute での起動に変更（ADR 0012）
- 式のスコープ問題が式評価全般に及ぶことを確認し、hint で案内するようにした（ADR 0013）
- `Process` を破棄した後に `Id` を読んでいた（`target.list` が失敗）
- 仕込みバグ BUG_04 が起動直後に一度だけ発火し、以後再現しなかった（引数の位相に依存していた）

次: Phase 2（Composite 第 1 群 + タスク対応）。

---

## 2026-08-25 — Phase 2（Composite 第 1 群 + タスク対応）完了

やったこと:

- `CompositeService`（`run-until` / `watch-until-change` / `trace-expr` / `dump` / `task-map`）
- `TaskNameResolver`（スレッド名 → エントリ関数の正規表現 → null の 3 段階）
- カーソルによる応答分割（`cursor.next`、10 分で失効、1 度きり）
- CLI に `run-until` / `watch-until-change` / `trace-expr` / `dump` / `task-map` と
  `thread select|freeze --task`、配列を返すコマンドの `--cursor`
- `samples/target` に BUG_03（別タスクからの想定外書き込み）を追加
- `tests/integration/phase2.ps1`（11 項目）
- 単体テスト 88 件（Core 43 / Cli 25 / Backend 20）

完了条件の確認（phase2.ps1 で 11 項目、fail=0）:

- BUG_03: `watch-until-change` が書き込み元を `task=B` 付きで返し、スタックに `Task_B_Main`
- BUG_04: `run-until --cond "g_ctx.state == 7" --expr ...` が 1 手で `state=7` と原因関数を返す
- `dump` が経路付きの平らな列（22 要素）を返し、上限 900 バイトで 6 件ずつに分割される
- カーソルは 1 度使うと `NOT_FOUND` になる
- `task-map` が A/B/C/D を当て、`thread select --task C` / `freeze --task C` が効く
- 知らないタスク名は候補付きで終了コード 5
- Composite が張ったブレークポイントが後に残らない

実装中に見つけて直したもの:

- カーソルを標準エラーに出していたため、`2>&1` で受け取ると JSON が壊れた（ADR 0015）
- `dump` を木で返すとページングできなかった（ADR 0014）
- `BreakpointLastHit` が返すのは束縛された子で、名前一致では自分のブレークポイントと
  照合できなかった。Tag と位置でも照合するようにした
- 仕込みバグ BUG_03 が、バグ無しビルドでも書き込んでいた

次: Phase 3（Skill と評価スイート v1）。

---

## 2026-08-25 — Phase 3（Skill と評価スイート v1）完了

やったこと:

- `skills/stakeout/SKILL.md`（design.md §17）。前提・調査の型・禁止事項・式の書き方・
  タスク指定・ページング・後始末・報告の型。実測値（1 ステップ 33 ms、
  トレース 2〜6 hits/s、データ BP 4 本）を根拠として本文に入れた
- `eval/cases/BUG_01,03,04,05.md`（症状 + 正解 + 判定）
- `eval/run.ps1`（Claude Code を起動して調査させ、判定・所要時間・RPC 回数を記録）

完了条件の確認（`eval/results/2026-08-25-v1.md`）:

| ケース | 判定 | 所要秒 | stakeout 呼び出し |
|---|---|---|---|
| BUG_01 | PASS | 174 | 23 |
| BUG_03 | PASS | 84 | 15 |
| BUG_04 | PASS | 180 | 18 |
| BUG_05 | PASS | 129 | 29 |

4 件とも 30 ターン以内・3 分以内。渡したのは症状の文と pid だけ。

**1 回目の測定は無効だった（ADR 0016）。** 4 件 PASS という結果は同じだったが、
記録を読むと (1) 症状の文が CP932 で化けて届いており、(2) エージェントが
`eval/cases/` の正解ファイルを読んでいた。結果だけを見ていたら気づかない種類の欠陥である。
文字コードの明示・正解の退避・無効化条件を入れてから測り直した。

次: Phase 4（Code Index と find-corruption）。

---

## 2026-08-25 — Phase 4（Code Index と find-corruption）完了

design.md §20 の Phase 4 は ADR 0007 で入れ替えた順序に従い、Code Index を先に実装した。

やったこと:

- `CodeIndex`（gtags を `--result=grep` で呼ぶ）と `WriteSiteDetector`（書き込み判定）
- `find-corruption`（静的な候補と動的な書き込みを突き合わせ、候補に無いものを名指しする）
- CLI に `code def|refs|writers` と `find-corruption`
- `samples/target` の BUG_03 を「隣接メンバへの範囲外書き込み」に変更
- `tests/integration/phase4.ps1`（8 項目）
- 単体テスト 114 件（Core 69 / Cli 25 / Backend 20）

完了条件の確認（phase4.ps1 で 8 項目、fail=0）:

- `code writers g_shared.counter` が代入行を列挙し、確信度 high/low を付ける
- `find-corruption` が `nl_stray_write`（task C）だけを「候補に無い書き込み」として名指しし、
  正規の `nl_bump_counter` は「静的な候補に一致」として区別する
- データ式は `{,,モジュール名}` 付きでも扱え、データ BP は後始末される
- Code Index が未設定なら `NOT_CONFIGURED` と設定キー名を返す

実装中に見つけて直したもの:

- **BUG_03 が静的に見える書き込みだった。** `g_shared.counter = value` は gtags で見つかるため、
  「候補に無い書き込み」を作れていなかった。範囲外書き込み（`scratch[4]` が `counter` を踏む）に変え、
  find-corruption が存在する理由が実際に検証されるようにした
- gtags が変数の定義も参照も索引していなかった（ADR 0017）
- モジュール修飾 `{,,NativeLib.dll}` を付けたままソースと照合していて候補がゼロになった
- 書式文字列 `printf("counter=%p")` を代入と誤判定していた

評価スイート（BUG_03）を新しい仕込みで測り直し、PASS を確認した（80 秒 / stakeout 11 回）。

次: Phase 5（DbgEng Backend）。**Debugging Tools for Windows の導入が前提**（ADR 0001）。

---

## 2026-08-25 — 現時点のまとめと、残っているもの

### 動いているもの

- Phase 0〜4 完了。単体テスト 114 件、統合検証 50 項目、評価スイート 4 ケースがすべて通る
- `log.tail`（design.md §14）を実装した。どのフェーズにも割り当てが無かったが、
  Skill が「根拠としてログを引く」ことを求めているため埋めた

### 最終確認（2026-08-25）

| 検証 | 結果 |
|---|---|
| `tests/run.ps1` | 114 件成功 |
| `phase1.ps1 -Bug BUG_04` | 15 項目成功 |
| `phase1.ps1 -Bug BUG_05` | 16 項目成功 |
| `phase2.ps1` | 11 項目成功 |
| `phase4.ps1` | 8 項目成功 |
| `eval/run.ps1` 全 4 ケース | 4/4 PASS |

### 残っているもの

| フェーズ | 状態 | 理由 |
|---|---|---|
| Phase 5（DbgEng Backend） | **着手できない** | Debugging Tools for Windows が未導入（ADR 0001）。開発機に dbgeng.dll も cdb.exe も無い |
| Phase 6（Trace DB） | 待ち | ADR 0007 により Phase 5 が前提。EnvDTE のトレースは 2〜6 hits/s で、この上に載せても価値が出ない |
| Phase 7（MCP アダプタ） | 保留 | design.md §4.3 の通り、シェルのあるクライアントでは CLI で足りる |
| Phase 8（拡張） | 保留 | design.md が「必要になったら」と定めている |

Phase 5 の着手には、ユーザーが Debugging Tools for Windows を導入する必要がある。
これは開発機の構成を変える操作なので、判断を仰いでから進める。

---

## 2026-08-25 — Phase 5 の前提を検証（着手には至らず）

「Phase 5 は着手できない」という判断そのものを疑い、確かめ直した。

**ADR 0001 の事実誤認が見つかった。** `dbgeng.dll` は Windows Kits には無いが、
**System32 には標準で入っている**（10.0.26100.1）。「無いから着手できない」は前提が違っていた。

`spike/DbgEngProbe` で System32 の dbgeng を実測した結果（ADR 0018）:

- `DebugCreate` は成功する
- **非侵入アタッチは動く**（1.4 秒でモジュール 6 件を列挙）
- **侵入アタッチが成立しない**。`WaitForEvent` が S_FALSE を返し続け、
  エンジンは `*** wait with pending attach` と出す

原因の切り分けとして、interop の vtable（非侵入アタッチ後に正しい値を返す）、
.NET の RCW（生の vtable 呼び出しでも同じ）、OS の許可
（`DebugActiveProcess` を直接呼ぶと正常にイベントが流れる）、
アタッチのフラグ、他の dbgeng.dll の有無を、それぞれ潰した。
**OS レベルでは動くのに dbgeng 経由だけが成立しない**という状態で、原因は特定できていない。

判断: **Phase 5 は Debugging Tools for Windows の導入を待つ。**
非侵入アタッチだけでは design.md §11.5 の価値（データ BP、速いトレース）が得られないため、
System32 版の上に Backend を作らない。

導入後にすぐ測り直せるよう、スパイクは `DBGENG_PATH` で dbgeng の場所を差し替えられる。

### 学んだこと

「無い」と書く前に、置き場所を 1 か所しか見ていないことを疑う。
ADR 0001 の 1 行が、Phase 5 を丸ごと「着手できない」に分類したまま残っていた。

---

## 2026-08-25 — Phase 7（MCP アダプタ）完了

やったこと:

- `Stakeout.Client` を切り出し（デーモンへの接続を CLI と MCP で共有）
- `Stakeout.Mcp`（stdio の MCP サーバー）。initialize / tools/list / tools/call / ping
- 道具は 10 個。**低レベル操作は載せない。**「調査 1 手」になっている複合コマンドと、
  それに要る最小限（attach / detach / wait / stack / eval / dump）だけ
- `tests/integration/phase7.ps1`（4 項目）と単体テスト 11 件

完了条件の確認（phase7.ps1 で 4 項目、fail=0）:

- ツール定義 3535 バイト（上限 4096）
- initialize → tools/list → attach → run-until → dump → detach が MCP のワイヤで通る
- `run_until` が `reachedTarget: true` と `g_ctx.state = 7` を返す
- `dump` が経路付き 45 要素を返す
- 評価できない式は `isError` として返り、hint が残る
- デタッチ後も Target が生きている

Claude Desktop 実機での確認はしていない。同じワイヤプロトコルを話すところまでを確かめた。

実装中に見つけて直したもの:

- **ツール定義の大きさを本体と検証で別々に測っていた**（3535 と 4943 に割れた）。
  本体は実際に送る形（非エスケープ）、検証は既定のエスケープ付きで測っていた。
  測り方を `McpTools.DescribeByteCount()` に寄せた。
  片方だけが上限を守っているつもりになる類の食い違いである
- デーモンの自動起動が MCP から効かなかった。探索がプロジェクト名 `Stakeout.Cli` を
  決め打ちしていたため。`Stakeout.*` の区間を置き換える形に直した

### 残っているもの

| フェーズ | 状態 |
|---|---|
| Phase 5（DbgEng Backend） | Debugging Tools for Windows の導入待ち（ADR 0018） |
| Phase 6（Trace DB） | Phase 5 が前提（ADR 0007） |
| Phase 8（拡張） | design.md が「必要になったら」と定義。着手の判断はユーザー |

---

## 2026-08-25 — 全体の最終確認

| 検証 | 結果 |
|---|---|
| `tests/run.ps1` | 125 件成功（Core 69 / Cli 25 / Backend 20 / Mcp 11） |
| `phase1.ps1 -Bug BUG_04` | 15 項目成功 |
| `phase1.ps1 -Bug BUG_05` | 16 項目成功 |
| `phase2.ps1` | 11 項目成功 |
| `phase4.ps1` | 8 項目成功 |
| `phase7.ps1` | 4 項目成功 |
| `eval/run.ps1` 全 4 ケース | 4/4 PASS |

統合検証を続けて回したとき、`phase4.ps1` が 1 度だけ 7/1 になった。
単独で走らせ直すと 2 回とも 8/8 で通った。前の実行のデーモンと Harness が
落ち切る前に次を始めたことによる競合と考えられる。
`tests/integration/README.md` に「1 本ずつ走らせること」を明記した。

---

## 2026-08-25 — stakeout doctor と、DbgEng 診断の訂正

### stakeout doctor（design.md §3.2 を道具が答える）

§3.2 の「実装前に必ず確認すること」は、8 項目の表を人が埋める形で放置されていた。
埋まらないまま実装が進む形になっていたので、**道具が答えられるものは道具が答える**ようにした。

実機での出力（samples/target に対して）:

| 項目 | 結果 |
|---|---|
| Q1 プロセス構成 | OK 単一プロセス |
| Q2 タスクとスレッドの対応 | OK 7 スレッド中 4 個（A/B/C/D） |
| Q3 同時実行スレッド数 | **不明**（1 回の観測では「常に」は言えない） |
| Q4 スレッド名 | 注意 付いていない |
| Q5 bitness | OK x64（`IsWow64Process2` で Target を直接判定） |
| Q6 dbgeng の有無 | 注意 System32 の同梱版だけ |
| Q7 昇格レベル | OK どちらも非昇格 |
| Q8 ASan | OK MSVC 14.44 / 14.50 |

実装中に直したもの:

- `SessionInfo.Bitness` が**デーモン自身の bitness** を返していた。WOW64 の Target を見誤る。
  `IsWow64Process2` で Target を直接判定するようにした
- Q3 の詳細が、シンボルの無いスレッド（生アドレス）を「実行中」に数えていた。
  実態より多く見えるので、待ち／実行中／判定不能に分けて数えるようにした

### DbgEng の診断を訂正（ADR 0019）

ADR 0018 に「初期イベントが来ない」と書いたのは**事実に反していた**。
エンジンの出力を全部読むと `*** attach succeeded` が出ており、モジュールも読み込まれ、
初期ブレークポイントも発生していた。

原因はスパイクの計測が **HRESULT を見ずに `out` の値だけを読んでいた**こと。
`GetNumberModules` は E_UNEXPECTED で失敗しており、`0` は観測結果ではなく初期値だった。
それを「モジュール 0 件」と読み、そこから 4 回の切り分けをすべて誤った前提で進めていた。

**結論（System32 の dbgeng を Phase 5 の土台にしない）は変えない。**
理由が「イベントが来ない」から「呼び出し側から停止を捉えられず、
セッションのコンテキストが確立しない」に変わった。

WinDbg（`winget install Microsoft.WinDbg`）を入れたが、その dbgeng.dll は
`C:\Program Files\WindowsApps` 配下で ACL により読み込めなかった。
SDK 版（`Windows Kits\10\Debuggers\x64`）が要る。

単体テスト 146 件（Core 90 / Cli 25 / Backend 20 / Mcp 11）。

---

## 2026-08-25 — 拡張性の点検（ADR 0020）

「言語や設計は拡張性に耐えるか」を点検した。

層構造（Rpc → Core → Daemon → Cli）は今日 4 回拡張して、いずれも機械的に済んだ
（find-corruption / log tail / doctor / MCP）。ここは実証されている。

一方 **`IDebuggerBackend` は実装が 1 つしか無く、未実証だった。** 実際に漏れが見つかった。
Visual Studio の式構文（`{,,DLL名}`、書式指定子 `,x`）が、Backend を知らないはずの
Core・Rpc・Skill に埋まっている。

とくに `WriteSiteDetector.StripModuleQualifier` は `}` を探す実装なので、
DbgEng の `NativeLib!g_shared` を渡すと何も剥がれず、**`code writers` が
エラーにならずに候補ゼロを返す**。今日すでに同じ形の失敗を 1 回踏んでいる。

**今は直さない。** 実装が 1 つしか無い状態で切った抽象は、その実装の形になる。
Backend が 1 つの間は実害もゼロ。Phase 5 着手時に、DbgEng の実物を見てから直す。
忘れないよう ADR 0020 にチェックリストを残し、design.md §6 と Phase 5 の前提条件から参照させた。

効率については、言語は律速ではないことを実測で確認した。
.NET のプロセス起動は約 30 ms で、調査 1 件（80〜190 秒）の 1% 未満。
支配的なのは Visual Studio の応答待ち（式評価 6.3 ms、break/go 33 ms、
トレース 1 ヒット 160〜400 ms）であり、これは言語の選択と無関係である。

---

## 2026-08-25 — メモリの直接読み取りを実装した（Phase 1 の埋め残し）

`stakeout mem` を足した。`mem.read` RPC、CLI、MCP（`stakeout_mem`）まで通した。

**これは新機能ではなく、埋め残しの回収である。** design.md §10.8 は EnvDTE の
`ReadMemory` を「式評価経由（遅い、上限 4 KiB）」= **可**と書いており、§14 には
手段（`*(unsigned char(*)[N])(ADDR)`）まで書いてある。にもかかわらず実装は
`UNSUPPORTED` を投げ、Capabilities も `ReadMemory = false` にしていた。
Phase 1 の作業項目に `mem.read` を書き落としたまま完了にしていた。

型付きの読み取り（`eval` / `dump`）で調査の大半は足りるが、**型が信用できない場面**
——構造体が壊れている、ヒープのヘッダを見たい——では生のバイト列が要る。
「無くても評価スイートが通る」ことを「要らない」の根拠にしていた。それは違う。

設計上の判断を 2 つした。

- **読めなかったバイトを 0 で埋めない。最初に読めなかった位置で切る。**
  未マップのページとゼロが並んだ領域を混ぜると、メモリ破壊の調査で誤った結論が出る。
  返る配列の長さが「ここまでは確かに読めた」を意味する。`length < requested` が
  そのまま「そこから先は読めない」を表す
- **アドレスだけでなく式も受ける**（`&g_ctx`）。生アドレスを得るためだけに
  eval を 1 往復させるのは、調査の手数を無駄に増やす。VS 固有の解釈は
  `VsValueParser` に閉じ込め、Core には `AddressParser`（`0x` 接頭辞のみ）を置いた（ADR 0020）

単体テスト 190 件（Core 107 / Backend 47 / Cli 25 / Mcp 11）。MCP の定義は 3882 バイト
（上限 4096）。**上限まで 214 バイトしか残っていない。** 次に道具を足すときは
既存の説明を削る必要がある。

**[要検証] VS 実機での確認が済んでいない。** `*(unsigned char(*)[N])(ADDR)` が
実際に評価できるか、`DataMembers` が要素を返すか、値がどの書式で返るかは、
Visual Studio を起動しないと確かめられない。`tests/integration/phase1.ps1` に
「メモリを読める」を足してあり、既知の値（`g_ctx.inner.flags` = 0xA5A5A5A5）との
突き合わせ、式とリテラルの一致、読めない番地でゼロ埋めしないことを見る。
**これが通るまで、この機能は動作未確認である。**

---

## 2026-08-25 — stakeout に改名した

公開前に名前を調べたら、同名かつ同じ発想の先行プロジェクトがあった（ADR 0021）。
`dbgd` / `dbg` を捨て、`stakeout` / `stakeoutd` / `stakeout-mcp` にした。
名前空間は `Stakeout.*`、設定は `.stakeout.json`、MCP のツールは `stakeout_*`。

11 プロジェクト・142 ファイル。単体テスト 190 件は全部通り、ビルドは 0 警告。
MCP の定義は 3937 バイト（上限 4096。接頭辞が 5 文字伸びた分だけ増えた）。

**移動の途中で csproj の `ProjectReference` が 3 件落ちた。** ディレクトリ単位の
`git mv` が OS に拒否され、ファイル単位に切り替えた際に不完全な複製が残ったのが原因。
ビルドが通らなくなって気づいたが、**他に落ちていないことは「気づかなかった」では
確認できない**ので、HEAD と全 142 ファイルの行数を突き合わせる検算を入れた。

実際に走らせた記録（eval の transcript、spike の results）は書き換えていない。
当時の CLI 名が残っている。直すと記録として嘘になる。
