# 0010. テストは `dotnet test` ではなく `tests/run.ps1` で走らせる

- 状態: 受理
- 日付: 2026-08-25
- 関連: design.md §19.1, 付録 A / ADR 0009

## 発見した事実

xunit v3 は VSTest ではなく Microsoft.Testing.Platform (MTP) で動く。
.NET SDK 10 では VSTest 経由の実行が明示的に拒否される。

```
error : Testing with VSTest target is no longer supported by
Microsoft.Testing.Platform on .NET 10 SDK and later.
```

MTP へのオプトイン（`global.json` の `test.runner`）を設定すると、
`dotnet test` は走るようになるが **0 件のテストしか検出しない**（終了コード 5）。

```
Stakeout.Core.Tests.dll (net8.0) 0 件のテストが実行されました
終了コード: 5
```

同じアセンブリを直接実行すると 31 件が検出され、すべて成功する。

```
> dotnet tests/Stakeout.Core.Tests/bin/Debug/net8.0/Stakeout.Core.Tests.dll
   Stakeout.Core.Tests  Total: 31, Errors: 0, Failed: 0, Skipped: 0
```

xunit.v3 を 4.0.0 と 3.2.2 の両方で試したが結果は変わらなかった。
SDK 10.0.201 の `dotnet test` と MTP の統合側の問題と考えられる。

## 選択肢

1. xunit v2 に戻す（VSTest で `dotnet test` が使える）
2. `dotnet test` を諦め、テストプロジェクトを直接実行する
3. SDK を 9 系に固定する

案 1 は、テストの書き味は変わらないが、v2 は既にメンテナンスモードであり、
いずれ同じ移行を迫られる。今払うか後で払うかの違いでしかない。

案 3 は開発機の SDK を縛る。実運用の環境が別の SDK を持つ可能性があり、
`global.json` で固定すると「SDK が無い」で全員が止まる。テストの実行方法のために
ビルド全体を人質に取る価値は無い。

## 決定

案 2 を採る。

- xunit v3 のテストプロジェクトは実行可能ファイルなので、そのまま実行する
- `tests/run.ps1` が全テストプロジェクトを走らせ、1 つでも落ちたら終了コード 1 を返す
- 開発の手引きと README のテスト手順を `tests/run.ps1` に変える
- 単一プロジェクトだけ走らせたいときは `dotnet run --project tests/Stakeout.Core.Tests`

`dotnet test` が将来動くようになったら、この ADR を覆す新しい ADR を書いて戻す。
`tests/run.ps1` はそのとき捨てられる程度の薄さに保つ。

## 影響

- design.md 付録 A の「`dotnet build` / `dotnet test`」を `tests/run.ps1` に改める
- CI を組むときはこのスクリプトを呼ぶ
