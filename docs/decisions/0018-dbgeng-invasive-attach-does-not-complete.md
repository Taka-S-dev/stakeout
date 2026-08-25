# 0018. dbgeng は System32 にある。ただし侵入アタッチが完了しない

- 状態: 受理
- 日付: 2026-08-25
- 検証: `spike/DbgEngProbe`
- 関連: ADR 0001（この ADR が事実誤認を訂正する）、design.md §11, §20 Phase 5

## ADR 0001 の誤りを訂正する

ADR 0001 に「dbgeng.dll / cdb.exe は無い。Phase 6（当時の番号）は
Debugging Tools for Windows の導入が前提」と書いた。**これは調査が不十分だった。**

`C:\Program Files (x86)\Windows Kits\10\Debuggers\` しか見ておらず、
**`C:\Windows\System32\dbgeng.dll` を見ていなかった**。実際には存在する。

```
C:\WINDOWS\system32\dbgeng.dll   10.0.26100.1   7,225,344 バイト
C:\WINDOWS\system32\dbghelp.dll
C:\WINDOWS\system32\dbgcore.dll
```

「無いから着手できない」という結論は、前提が違っていた。
Phase 5 が止まっていた理由を、実際に動かして確かめ直した。

## 実際に確かめたこと

`spike/DbgEngProbe` で、System32 の dbgeng に対して次を測った。

| # | 内容 | 結果 |
|---|---|---|
| D1 | `DebugCreate` でクライアント生成 | **OK** |
| D2 | 非侵入アタッチ（`DEBUG_ATTACH_NONINVASIVE`） | **OK**（1.4 秒でモジュール 6 件を列挙） |
| D2' | 侵入アタッチ（`DEBUG_ATTACH_DEFAULT`） | **不成立**。`WaitForEvent` が S_FALSE を返し続ける |
| D3 | シンボル解決 | 侵入アタッチが成立しないため到達せず |

エンジン自身の診断出力（`IDebugOutputCallbacks`）を拾うと、こう出ていた。

```
*** wait with pending attach
```

アタッチ要求は受理されるが、初期イベントが永久に来ない。

## 切り分けたこと

原因を取り違えないよう、疑わしい層を 1 つずつ潰した。

| 疑い | 検証 | 結果 |
|---|---|---|
| interop（vtable の並び）が違う | 非侵入アタッチ後に `GetNumberModules` / `GetExecutionStatus` が正しい値を返すか | **正しい**。interop は問題ない |
| .NET の RCW が呼び出しを別スレッドへ回している | RCW を使わず生の vtable 呼び出しで同じ手順を実行 | **同じ結果**。RCW は原因ではない |
| OS が侵入デバッグを許していない | `DebugActiveProcess` / `WaitForDebugEvent` を直接呼ぶ | **正常に動く**。CREATE_PROCESS / LOAD_DLL / CREATE_THREAD を受け取れた |
| アタッチのフラグが違う | `INVASIVE_NO_INITIAL_BREAK` / `EXISTING` を試す | 変わらない（`EXISTING` は 0xD0000353） |
| 別の dbgeng.dll がある | ファイルシステム全体を検索 | System32 の 1 つだけ |

**OS レベルの侵入デバッグは動くのに、dbgeng 経由だけが成立しない。**
この差の理由は特定できていない。

## 決定

1. **System32 の dbgeng は Phase 5 の土台にしない。** 侵入アタッチが成立しない以上、
   ブレークポイントも実行制御も載らない。非侵入アタッチだけでは、
   design.md §11.5 が挙げる価値（データ BP、速いトレース）のどれも得られない
2. **Phase 5 は Debugging Tools for Windows の導入を待つ。**
   design.md §11.2 が指定している構成（Debuggers\x64 の dbgeng.dll + dbghelp + symsrv）は、
   本来これを想定している。System32 の複製は OS の内部用途向けであり、
   一般のユーザーモードデバッグ用に提供されているものではない
3. **導入したらすぐ測り直せるようにしておく。** スパイクは `DBGENG_PATH` を受け、
   任意のディレクトリの dbgeng.dll を読み込む。

   ```
   DBGENG_PATH="C:\Program Files (x86)\Windows Kits\10\Debuggers\x64" \
     dotnet run --project spike/DbgEngProbe -- --pid <pid>
   ```

   `D2 OK` と `D3 OK` が出れば着手してよい。出なければ、原因は SDK の有無ではなく
   この環境固有の何か（セキュリティ製品によるブレークイン注入の阻害など）であり、
   別の ADR を起こして判断し直す

## 学んだこと

**「無い」と書く前に、置き場所を 1 か所しか見ていないことを疑う。**
ADR 0001 は「Windows Kits に無い」を「無い」と書いた。その 1 行が、
Phase 5 を丸ごと「着手できない」に分類し、そのまま残っていた。

存在確認は安い。存在しないことの確認は安くない。
安いほうを済ませたところで結論を出していた。
