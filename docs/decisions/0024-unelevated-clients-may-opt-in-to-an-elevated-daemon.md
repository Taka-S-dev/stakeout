# 0024. 通常権限のクライアントから管理者デーモンへの接続は、ユーザー設定でだけ開ける

- 状態: 受理
- 日付: 2026-09-15
- 関連: design.md §10.3, §16, ADR 0023

## 背景

製品の Visual Studio 2019 は管理者で動いている。デーモンは VS と同じ昇格レベルでないと
COM が通らない（§10.3）ので、デーモンも管理者になる。一方、エージェントを動かすシェルは
通常権限で起動されることが多く、実機では名前付きパイプの接続が `UnauthorizedAccessException` で落ちた。

これは「エージェントが自動で調べる」ための最初の一歩を塞いでいる。繋がりさえすれば、その先は
ADR 0023 の評価で通っている流れと同じである。

## 発見した事実

管理者で起動したデーモン（`stakeoutd --detach`、`-Verb RunAs`）に、通常権限の同じユーザーから繋いだ。

| クライアント | 結果 |
|---|---|
| CLI（`PipeOptions.CurrentUserOnly`） | `Access to the path is denied` |
| 生の `NamedPipeClientStream`、`CurrentUserOnly` 無し | `Access to the path is denied` |

`CurrentUserOnly` 無しでも拒否されるので、**ACL そのものが拒否している**。クライアント側の
所有者検査（`CurrentUserOnly` が行う「所有者 == 自分」の確認）に落ちる以前の問題である。

理由は .NET の `CurrentUserOnly` の作り方にある。サーバー側はトークンの **Owner** SID に FullControl を与える。
管理者に昇格したトークンの Owner は本人ではなく `BUILTIN\Administrators` になる。通常権限のトークンでは
Administrators グループが deny-only なので、同じユーザーでも開けない。

## 決定

1. 設定 `pipe.allowUnelevatedClients`（既定 false）を足す。true のとき、デーモンはパイプの **所有者と ACL を
   ユーザー本人の SID**（`WindowsIdentity.User`）にする。グループ（Administrators）には与えない。
   管理者の別ユーザーが繋げてはいけない
2. **この設定はユーザー設定（`%APPDATA%\stakeout\stakeout.json`）にしか書けない。** `.stakeout.json` に
   あれば `ConfigException` で止まる。黙って無視すると「書いたのに効かない」、黙って効かせると
   「クローンしただけで管理者デバッガへの経路が開く」になる
3. 所有者を本人にするので、通常権限クライアントの `CurrentUserOnly` の所有者検査はそのまま通る。
   一方、**管理者のクライアント**は Owner が Administrators なので、その検査に落ちる。
   クライアントは `UnauthorizedAccessException` を受けたら `CurrentUserOnly` 無しで繋ぎ直し、
   ACL を自前で確かめる（`PipeOwnershipCheck`）。条件は .NET の検査より緩めない:
   所有者は本人か Administrators、Allow は本人・Administrators・SYSTEM だけ。
   通らなければ「自分のデーモンと確かめられない」と言って繋がない
4. デーモンは接続ごとに相手の pid・実行ファイル名・昇格状態をログに残す。起動時のログに ACL の種別を出す
5. `allowProcesses` はそのまま効く。ACL を開いても、アタッチできる対象は変わらない
6. 接続拒否の hint に、この設定を書いてデーモンを起動し直す手順を入れる

## 採らなかった案

- **既定で本人 SID 宛てにする。** 通常権限のプロセスが管理者のデバッガを操作できるのは、
  実質的に管理者権限でコードを実行する経路である。使う人が明示的に開く形にする
- **ヘルパープロセスで権限を分ける。** 操作の分類・別 RPC・監査の設計が要る。
  今の目的（開発機でエージェントに調査させる）には過剰
- **Everyone や Authenticated Users に開く。** 論外

## 影響

- `StakeoutConfig.Pipe`、`ConfigLoader.RejectUserOnlyKeys`、`Stakeout.Daemon/PipeAccess`、
  `Stakeout.Client/PipeOwnershipCheck`、`DaemonClient.TryConnectVerifiedAsync`
- design.md §10.3、§16 に追記
- **[要検証]** 管理者デーモン + 設定 on に対して、通常権限 CLI と管理者 CLI の両方が繋がること。
  設定 off では従来どおり拒否されること。`tests/integration/elevation.ps1` を管理者シェルから流す。
  設定 off の拒否と、その原因が ACL であることは実機で確認済み。設定 on 側は未確認。
  特に `PipeSecurity.SetOwner(本人 SID)` が管理者プロセスで通るかは要確認。
  通らなければ所有者は Administrators のままにする（`PipeOwnershipCheck` はそれも許す）
