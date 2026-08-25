# 0019. dbgeng の診断を訂正する。イベントは届いている

- 状態: 受理（ADR 0018 の診断部分を訂正する）
- 日付: 2026-08-25
- 検証: `spike/DbgEngProbe`
- 関連: ADR 0018, design.md §11, §20 Phase 5

## ADR 0018 の何が間違っていたか

ADR 0018 に「アタッチ要求は受理されるが、初期イベントが永久に来ない」と書いた。
**これは事実に反する。**

エンジン自身の診断出力（`IDebugOutputCallbacks`）を全部読むと、こう出ていた。

```
*** attach succeeded
*** Create process 18010
ModLoad: ... Harness.exe
ModLoad: ... ntdll.dll
ModLoad: ... NativeLib.dll
Exception 80000003 at 00007ffa701dece0   （初期ブレークポイント）
>>> Event status 9                        （DEBUG_STATUS_IGNORE_EVENT）
>> Continue with 10002                    （DBG_CONTINUE）
```

**アタッチは成功し、モジュールも読み込まれ、初期ブレークポイントも発生していた。**
エンジンはそれらを一つずつ「無視して継続」していただけである。

## なぜ間違えたか

スパイクの計測が **HRESULT を見ずに `out` の値だけを読んでいた**。

```csharp
control.GetExecutionStatus(out var status);      // 戻り値を捨てている
symbols.GetNumberModules(out var loaded, out _); // 同上
Console.WriteLine($"status={status} modules={loaded}");
```

戻り値を出すようにしたら、こうだった。

```
status=6(hr=0x00000000) modules=0(hr=0x8000FFFF)
```

`GetNumberModules` は **失敗していた**（E_UNEXPECTED）。`loaded` の 0 は
観測結果ではなく初期値である。それを「モジュールが 0 件」と読み、
そこから「アタッチできていない」と結論していた。

**失敗した呼び出しの出力値を、観測結果として読んでいた。**
この 1 点で、4 回の切り分け（interop・RCW・OS・フラグ）すべての解釈が狂っていた。

## 現時点で分かっていること

| 事実 | 根拠 |
|---|---|
| アタッチは成功する | エンジンが `*** attach succeeded` と出す。ModLoad も出る |
| 初期ブレークポイントも発生している | `Exception 80000003` |
| エンジンはそれを無視して継続する | `Event status 9`（IGNORE_EVENT）→ `Continue with 10002` |
| `WaitForEvent` は呼び出し側に停止を返さない | 常に S_FALSE（タイムアウト） |
| `GetExecutionStatus` は S_OK で BREAK(6) を返す | エンジンは「停止している」と言う |
| それでもシンボル・モジュール照会は E_UNEXPECTED | セッションに現在のプロセス／スレッドが設定されていない |

`AddEngineOptions(DEBUG_ENGOPT_INITIAL_BREAK)` と
`SetInterrupt(DEBUG_INTERRUPT_ACTIVE)` を試したが、どちらも状況を変えなかった
（どちらも S_OK を返すが、`WaitForEvent` は S_FALSE のまま）。

**「エンジンは BREAK と言うのに、セッションには現在のコンテキストが無い」**
という食い違いが残っている。原因は特定できていない。

## 決定

1. ADR 0018 の**結論は変えない。** System32 の dbgeng を Phase 5 の土台にしない。
   理由は「イベントが来ない」ではなく、**呼び出し側から停止を捉えられず、
   セッションのコンテキストが確立しないため**である
2. Phase 5 に着手するなら、まず `spike/DbgEngProbe` で `D2 OK` / `D3 OK` が出ることを
   確認する。出ないまま Backend を書き始めない
3. WinDbg（`winget install Microsoft.WinDbg`）を入れたが、その dbgeng.dll は
   `C:\Program Files\WindowsApps` 配下にあり、**ACL で読み込めなかった**（error=5）。
   SDK 版（`Windows Kits\10\Debuggers\x64`）を入れる必要がある

## 学んだこと

**戻り値を見ずに `out` の値を読まない。** 失敗した呼び出しの出力は初期値であり、
観測結果ではない。それを観測結果として扱うと、間違った事実の上に切り分けが積み上がる。
今回は「interop が悪いのか」「RCW が悪いのか」「OS が悪いのか」を順に潰したが、
**潰していた対象は最初から無関係だった**。

計測器を疑う順番を間違えていた。ADR 0016（評価スイート自体を検証する）で
同じことを書いておきながら、同じ日に同じ間違いをした。
今回は「計測器が別のものを測っていた」ではなく「計測器が壊れた値を読んでいた」だが、
根は同じである。**測る前に、測れていることを確かめる。**
