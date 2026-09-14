<#
.SYNOPSIS
  Phase 1（EnvDTE Backend）の統合検証（design.md §20 Phase 1 の完了条件）。

.DESCRIPTION
  実際の Visual Studio と samples/target の Harness.exe を使って、
  アタッチから式評価・条件付きブレークポイント・例外ブレーク・デタッチまでを通す。
  CI では動かせない。手で走らせる。

.PARAMETER VsPid
  使う Visual Studio の pid。省略時は起動中の VS が 1 つなら自動で決まる。

.EXAMPLE
  # 前提: Visual Studio を起動し、スタートウィンドウを抜けてモーダルが無い状態にしておく
  pwsh tests/integration/phase1.ps1
#>
[CmdletBinding()]
param(
    [int]$VsPid = 0,
    [string]$Bug = 'BUG_04'
)

$ErrorActionPreference = 'Stop'

# stakeout は UTF-8 で書く。Windows PowerShell の既定（CP932）のままだと日本語が化ける
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stakeout = Join-Path $root 'src\Stakeout.Cli\bin\Debug\net8.0\stakeout.exe'
$targetDir = Join-Path $root "samples\target\build\x64-$Bug"
$harness = Join-Path $targetDir 'Harness.exe'

if (-not (Test-Path $stakeout)) { throw "stakeout.exe がありません。dotnet build を実行してください: $stakeout" }
if (-not (Test-Path $harness)) { throw "Harness.exe がありません。samples/target/build.ps1 -Bug $Bug を実行してください" }

# 検証ごとに専用のパイプを使い、開発中のデーモンと混ざらないようにする
$env:STAKEOUT_PIPE = "stakeout-phase1-$PID"

$script:pass = 0
$script:fail = 0
$script:harnessProcess = $null

function Step {
    param([string]$Name, [scriptblock]$Body)

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    try {
        & $Body
        $script:pass++
        Write-Host "  OK" -ForegroundColor Green
    }
    catch {
        $script:fail++
        Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Dbg {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    # stakeout は失敗時に標準エラーへ書く。ErrorActionPreference = Stop のままだと
    # 2>&1 で拾った標準エラーが終了エラーになり、想定内の失敗まで検証が止まる
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $stakeout @Arguments 2>&1
        $script:lastExit = $LASTEXITCODE
        return ($output | Out-String)
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function DbgJson {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $text = Dbg @Arguments --json
    if ($script:lastExit -ne 0) { throw "stakeout $($Arguments -join ' ') が exit $script:lastExit で失敗しました: $text" }

    # --json は封筒を返す（ADR 0015）。中身は data に入っている
    $script:lastEnvelope = $text | ConvertFrom-Json
    return $script:lastEnvelope.data
}

<#
  ConvertFrom-Json は要素 1 個の配列を単一オブジェクトに畳んでしまい、
  .Count が取れなくなる。配列として扱う箇所では代入時に @() で包むこと。
  関数から @() を返しても、PowerShell が出力時に展開してしまうので意味がない。
#>

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    Write-Host "target : $harness"
    Write-Host "stakeout    : $stakeout"
    Write-Host "pipe   : $($env:STAKEOUT_PIPE)"

    Step 'Harness を起動する' {
        # 標準出力を必ずファイルに逃がす。既定では Harness がこのスクリプトの
        # 標準出力ハンドルを引き継ぎ、スクリプトが終わっても出力パイプが閉じない
        $harnessLog = Join-Path $targetDir 'phase1-harness.log'

        # --slow: 条件付きブレークポイントは 1 ヒットごとに VS が式を評価するため、
        # 毎秒 27 万回呼ばれる関数のままでは現実的な時間で条件を満たせない
        $harnessArgs = @('phase1', '--slow')

        $script:harnessProcess = Start-Process -FilePath $harness -ArgumentList $harnessArgs `
            -WorkingDirectory $targetDir -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $harnessLog -RedirectStandardError "$harnessLog.err"

        Start-Sleep -Seconds 2
        Assert (-not $script:harnessProcess.HasExited) 'Harness がすぐに終了した'
        Write-Host "  harness pid=$($script:harnessProcess.Id)"
    }

    Step 'デーモンが起動して状態を返す' {
        $status = DbgJson status
        Assert ($status.pid -gt 0) 'デーモンの pid が取れない'
        Write-Host "  stakeout pid=$($status.pid) backend=$($status.configuredBackend)"
    }

    Step 'allowlist が効いている（Harness だけが許可される）' {
        $targets = @(DbgJson targets)
        Assert ($targets.Count -ge 1) 'allowlist に一致する候補がない'
        Assert (@($targets | Where-Object { -not $_.allowed }).Count -eq 0) 'allowlist 外が混ざっている'
        Write-Host "  allowed: $(($targets | ForEach-Object { "$($_.name)($($_.pid))" }) -join ', ')"
    }

    Step 'Native のみでアタッチする' {
        $attachArgs = @('attach', '--pid', $script:harnessProcess.Id)
        if ($VsPid -gt 0) { $attachArgs += @('--vs-pid', $VsPid) }

        $session = DbgJson @attachArgs
        Assert ($session.pid -eq $script:harnessProcess.Id) 'アタッチ先の pid が違う'
        Assert ($session.backend -eq 'envdte') "backend が envdte でない: $($session.backend)"
        Write-Host "  session=$($session.sessionId) state=$($session.state)"
    }

    Step 'ブレークポイントで停止させる' {
        # 3 の次は 0 のはずが 7 に飛ぶ。state==7 の瞬間だけ止める。
        # 行番号は決め打ちにしない。ソースを直すたびに検証が壊れる
        $source = Join-Path $root 'samples\target\NativeLib\nativelib.c'
        $line = (Select-String -Path $source -Pattern 'g_ctx\.tick\+\+;' | Select-Object -First 1).LineNumber
        Assert ($null -ne $line) 'nativelib.c に g_ctx.tick++ の行が無い'

        # BUG_04 のときだけ条件付きにする。他のビルドでは state==7 にならない
        $bp = if ($Bug -eq 'BUG_04') {
            DbgJson bp set "nativelib.c:$line" --cond 'g_ctx.state == 7'
        }
        else {
            DbgJson bp set "nativelib.c:$line"
        }
        Assert ($bp.breakpointId -gt 0) 'ブレークポイントの ID が振られていない'
        Write-Host "  bp #$($bp.breakpointId) $($bp.location) cond=$($bp.condition)"

        Dbg continue | Out-Null
        $wait = DbgJson wait --timeout 30
        Assert $wait.stopped '30 秒待っても停止しなかった'
        Write-Host "  stopped: $($wait.stop.reason) thread=$($wait.stop.threadId) at $($wait.stop.description)"
    }

    Step '全スレッドのスタックが取れる' {
        $stacks = @(DbgJson stack --all)
        Assert ($stacks.Count -ge 3) "スレッドが 3 本以上見えない: $($stacks.Count)"

        $named = @($stacks | Where-Object {
            $_.frames | Where-Object { $_.function -like '*Task_*_Main*' }
        })
        Assert ($named.Count -ge 1) 'Task_*_Main のフレームが見つからない'
        Write-Host "  threads=$($stacks.Count)"
    }

    Step 'C の構造体メンバを読める' {
        $state = DbgJson eval 'g_ctx.state'
        if ($Bug -eq 'BUG_04') {
            # 条件付きブレークポイントで止めたので、この瞬間だけ 7 になっている
            Assert ($state.value -eq '7') "state が 7 でない: $($state.value)"
        }

        $flags = DbgJson eval 'g_ctx.inner.flags'
        Assert ($flags.value -match '2779096485|A5A5A5A5') "inner.flags が読めない: $($flags.value)"

        $name = DbgJson eval 'g_ctx.name' --format s
        Write-Host "  state=$($state.value) flags=$($flags.value) name=$($name.value)"
    }

    Step '構造体を展開できる' {
        $ctx = DbgJson eval 'g_ctx'
        Assert ($ctx.variablesReference -gt 0) 'g_ctx が展開可能になっていない'

        $members = @(DbgJson expand $ctx.variablesReference)
        $names = $members | ForEach-Object { $_.name }
        Assert ($names -contains 'state') "state メンバが無い: $($names -join ',')"
        Assert ($names -contains 'inner') "inner メンバが無い: $($names -join ',')"
        Write-Host "  members: $($names -join ', ')"
    }

    Step 'メモリを読める' {
        # EnvDTE にメモリを読む API は無く、式評価で代替している（design.md §14）。
        # 表示文字列を解釈して戻すので、**既知の値と突き合わせないと検証にならない**
        $flags = DbgJson mem '&g_ctx.inner.flags' -n 4
        Assert ($flags.length -eq 4) "4 バイト読めていない: length=$($flags.length)"
        Assert ($flags.hex -eq 'a5 a5 a5 a5') "既知の値と違う: $($flags.hex)"
        Write-Host "  &g_ctx.inner.flags -> $($flags.hex)"

        # アドレスのリテラルでも同じ場所が読めること
        $ptr = DbgJson eval '&g_ctx.inner.flags'
        Assert ($ptr.address -gt 0) "eval がアドレスを返していない: $($ptr.value)"

        $literal = DbgJson mem ('0x{0:x}' -f $ptr.address) -n 4
        Assert ($literal.hex -eq $flags.hex) "式とリテラルで結果が違う: $($literal.hex)"

        # 文字列も読めること
        $name = DbgJson eval 'g_ctx.name' --format s
        $text = ($name.value -replace '^.*?"(.*?)".*$', '$1')
        if ($text -and $text.Length -ge 3) {
            $raw = DbgJson mem '&g_ctx.name' -n 16
            Assert ($raw.ascii.StartsWith($text.Substring(0, 3))) `
                "名前が読めない: ascii=$($raw.ascii) expected head=$($text.Substring(0, 3))"
            Write-Host "  &g_ctx.name -> $($raw.ascii)"
        }

        # **読めない場所を 0 で埋めない。** 埋めると未マップとゼロ領域が区別できなくなる
        $bad = DbgJson mem '0x10' -n 16
        Assert ($bad.length -eq 0) "読めないはずの番地で $($bad.length) バイト返した: $($bad.hex)"
        Write-Host "  0x10 -> length=$($bad.length)（ゼロ埋めしていない）"
    }

    Step 'ローカル変数と引数が取れる' {
        $stack = @(DbgJson stack --depth 3)
        $frame = $stack[0].frames[0]
        Write-Host "  frame #$($frame.frameId) $($frame.function)"

        $locals = @(DbgJson locals --frame $frame.frameId)
        Assert ($null -ne $locals) 'ローカル変数が取れない'
    }

    Step 'スレッドを一覧して選び直せる' {
        $threads = @(DbgJson threads)
        Assert ($threads.Count -ge 3) "スレッドが少なすぎる: $($threads.Count)"

        $other = $threads | Where-Object { -not $_.isCurrent } | Select-Object -First 1
        $selected = DbgJson thread select $other.threadId
        Assert ($selected.threadId -eq $other.threadId) 'スレッドを選び直せない'
    }

    Step 'ブレークポイントを一覧して削除できる' {
        $list = @(DbgJson bp list)
        Assert ($list.Count -ge 1) 'ブレークポイントが一覧に出ない'

        $cleared = DbgJson bp clear
        Assert ($cleared.removed -ge 1) '削除されていない'

        $after = @(DbgJson bp list)
        Assert ($after.Count -eq 0) '削除後も残っている'
    }

    Step '停止を待っている間も status が返る' {
        # 待ちを直列化すると、停止するまで stakeout status すら返らなくなる（design.md §7.2）
        Dbg continue | Out-Null

        $waiter = Start-Job -ArgumentList $stakeout, $env:STAKEOUT_PIPE -ScriptBlock {
            param($exe, $pipe)
            $env:STAKEOUT_PIPE = $pipe
            & $exe wait --timeout 15 | Out-Null
        }

        try {
            Start-Sleep -Seconds 2
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $status = DbgJson daemon status
            $sw.Stop()

            Assert ($status.pid -gt 0) '待機中に status が返らない'
            Assert ($sw.Elapsed.TotalSeconds -lt 5) "待機中の status が遅すぎる: $($sw.Elapsed.TotalSeconds) 秒"
            Write-Host "  status は $([Math]::Round($sw.Elapsed.TotalSeconds, 2)) 秒で返った"
        }
        finally {
            $waiter | Wait-Job -Timeout 30 | Out-Null
            $waiter | Remove-Job -Force
        }
    }

    Step '停止中でないと読み取り系が PRECONDITION で断られる' {
        Dbg bp clear | Out-Null
        Dbg continue | Out-Null

        # 再開が反映されるまでに間がある。決め打ちのスリープではなく状態を見る
        $text = ''
        $deadline = (Get-Date).AddSeconds(10)
        do {
            $text = Dbg threads
            if ($script:lastExit -eq 4) { break }
            Start-Sleep -Milliseconds 300
        } while ((Get-Date) -lt $deadline)

        Assert ($script:lastExit -eq 4) "終了コードが 4 (PRECONDITION) でない: $script:lastExit"
        Assert ($text -match 'PRECONDITION') "PRECONDITION が返っていない: $text"
    }

    Step 'pause と continue が冪等である' {
        Dbg pause | Out-Null
        Assert ($script:lastExit -eq 0) 'pause が失敗した'

        Dbg pause | Out-Null
        Assert ($script:lastExit -eq 0) '2 回目の pause が失敗した（冪等でない）'

        Dbg continue | Out-Null
        Dbg continue | Out-Null
        Assert ($script:lastExit -eq 0) '2 回目の continue が失敗した（冪等でない）'
    }

    if ($Bug -eq 'BUG_05') {
        Step '例外ブレークで BUG_05 の発生元の C 行に止まる' {
            Dbg bp clear | Out-Null

            # 式評価は停止中にしか通らない。先に止める
            Dbg pause | Out-Null
            $paused = DbgJson wait --timeout 15
            Assert $paused.stopped '中断できなかった'

            $result = DbgJson bp exceptions --on --codes C0000005
            Assert $result.breakWhenThrown '例外ブレークを有効にできない'

            # 時間に頼らず、デバッガの式評価からバグを踏ませる。
            # 検証は再現待ちではなく、狙って起こしたものを観測する形にする。
            # 式は停止しているフレームのスコープで解決されるので、
            # 別モジュールのグローバルにはモジュール修飾が要る（ADR 0006）
            $trigger = DbgJson eval '{,,NativeLib.dll}g_crash_requested = 1'
            Assert ($trigger.isValid) "クラッシュ要求を書き込めない: $($trigger.value)"
            Write-Host "  g_crash_requested = $($trigger.value)"

            Dbg continue | Out-Null

            $wait = DbgJson wait --timeout 40
            Assert $wait.stopped '40 秒待ってもアクセス違反で止まらなかった'
            Assert ($wait.stop.reason -eq 'Exception') "停止理由が例外でない: $($wait.stop.reason)"

            $stack = @(DbgJson stack --depth 5)
            $functions = $stack[0].frames | ForEach-Object { $_.function }
            Assert (($functions -join ' ') -match 'nl_deref|nl_read_config') `
                "発生元の C 関数がスタックに無い: $($functions -join ', ')"
            Write-Host "  停止位置: $($functions[0])"
        }
    }

    if ($Bug -eq 'BUG_05') {
        # 未処理のアクセス違反で止まったままデタッチすると、例外は Target に戻り、Target は自分のバグで落ちる。
        # これは stakeout が殺したのではない。「生きている」を期待すると毎回落ちる（2026-09-15 に 2 回続けて確認）。
        # 代わりに、落ちた理由がそのアクセス違反であることを確かめる
        Step 'デタッチすると Target は自分のアクセス違反で終わる' {
            Dbg detach | Out-Null
            Assert ($script:lastExit -eq 0) 'detach が失敗した'

            $exited = $script:harnessProcess.WaitForExit(10000)
            Assert $exited 'アクセス違反で止まっていたのに、デタッチ後も終わらない'

            $code = '0x{0:X8}' -f $script:harnessProcess.ExitCode
            Write-Host "  exit code: $code"
            Assert ($code -eq '0xC0000005') "アクセス違反以外の理由で終わった: $code"
        }
    }
    else {
        Step 'デタッチしても Target が生きている' {
            Dbg detach | Out-Null
            Assert ($script:lastExit -eq 0) 'detach が失敗した'

            Start-Sleep -Seconds 1
            $script:harnessProcess.Refresh()
            Assert (-not $script:harnessProcess.HasExited) 'デタッチで Target が死んだ'
        }
    }
}
finally {
    Dbg daemon stop | Out-Null

    if ($script:harnessProcess -and -not $script:harnessProcess.HasExited) {
        $script:harnessProcess.Kill()
    }

    Write-Host ""
    Write-Host "pass=$script:pass fail=$script:fail" -ForegroundColor ($(if ($script:fail -eq 0) { 'Green' } else { 'Red' }))
}

exit ($(if ($script:fail -eq 0) { 0 } else { 1 }))
