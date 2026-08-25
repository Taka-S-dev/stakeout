# spike/DbgEngProbe

DbgEng（`dbgeng.dll`）を Phase 5 の Backend にできるかを確かめる使い捨てコード。
結論は `docs/decisions/0018-dbgeng-invasive-attach-does-not-complete.md`。

## 使い方

```
# 検証対象を起動しておく
samples/target/build/x64-BUG_03/Harness.exe probe --slow

dotnet run --project spike/DbgEngProbe -- --pid <pid>            # 侵入アタッチ（RCW 経由）
dotnet run --project spike/DbgEngProbe -- --pid <pid> --raw      # 侵入アタッチ（生 vtable）
dotnet run --project spike/DbgEngProbe -- --pid <pid> --os       # Win32 の DebugActiveProcess 直叩き

DBGENG_MODE=noninvasive dotnet run --project spike/DbgEngProbe -- --pid <pid>
```

## Debugging Tools for Windows を入れたら

`DBGENG_PATH` にそのディレクトリを指すと、System32 版ではなくそちらを読み込む。

```
DBGENG_PATH="C:\Program Files (x86)\Windows Kits\10\Debuggers\x64" \
  dotnet run --project spike/DbgEngProbe -- --pid <pid>
```

`D2 OK` と `D3 OK` が出れば、Phase 5 に着手できる。
