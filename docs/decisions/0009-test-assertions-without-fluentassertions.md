# 0009. テストのアサーションに FluentAssertions を使わない

- 状態: 受理
- 日付: 2026-08-25
- 関連: design.md §19.1（技術選定）

## 背景

design.md §19.1 はテストに「xUnit + FluentAssertions」を選定している。

## 発見した事実

FluentAssertions はバージョン 8.0 以降、ライセンスが Xceed のものに変わり、
**商用利用に有償ライセンスが必要**になった。現在の最新は 8.10.0 である。
無償で使えるのは 7.x 系までで、そちらは新しい .NET への追従が止まる。

この基盤は商用利用に当たる。
ライセンス費用を発生させる依存を、アサーションの書き味のためだけに
入れる理由が無い。

## 選択肢

1. FluentAssertions 8.x を有償ライセンスで使う
2. FluentAssertions 7.x に固定する
3. xUnit 標準の `Assert` を使う
4. Shouldly や AwesomeAssertions（FluentAssertions 7 系のフォーク）を使う

案 2 は、いずれ .NET の更新で行き詰まるのを先送りにするだけである。
案 4 は選択肢として妥当だが、依存を 1 つ増やす価値があるほど
テストコードが複雑になる見込みが今は無い。

## 決定

案 3 を採る。xUnit v3 標準の `Assert` を使い、アサーションライブラリを入れない。

- 可読性が問題になったら、そのときに案 4 を再検討する。この ADR を覆す新しい ADR を書く
- テストランナーは xunit.v3 4.0.0。.NET SDK 10 では VSTest 経由が使えないため Microsoft.Testing.Platform で走らせる

## 影響

- design.md §19.1 の「xUnit + FluentAssertions」を「xUnit v3」に改める
