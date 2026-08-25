# spike/

使い捨ての検証コード（ADR 0003）。**製品コードから参照しないこと。**

`DteProbe` は design.md の `[要検証]` を潰すためだけに書いた。抽象化されていないし、
テストも無い。結論は `docs/decisions/0004`〜`0008` に移してあり、
このディレクトリは「その結論をどう出したか」の記録として残しているだけである。

## 使い方

```
dotnet build spike/DteProbe

# 起動中の Visual Studio と、その DTE が応答するかを見る
dotnet run --project spike/DteProbe -- list

# S1〜S5 をまとめて実行する
dotnet run --project spike/DteProbe -- probe --pid <Harness.exe の pid> --vs-pid <VS の pid> \
    --trace-seconds 15 --event-rounds 20 --out spike/results/run.json

# アタッチ → デタッチで Target が生き残るかだけを見る
dotnet run --project spike/DteProbe -- detach-test --pid <pid> --vs-pid <pid> [--leave-data-bp]
```

前提:

- `samples/target/build.ps1` で Harness.exe をビルドし、起動しておく
- Visual Studio を起動し、**スタートウィンドウを抜けて、モーダルダイアログが無い状態**にしておく
  （そうでないと DTE は一切応答しない。ADR 0004）

## results/

各実行の生の出力。ADR の根拠。`spike-final.json` が S1〜S5 すべて成功した回。
