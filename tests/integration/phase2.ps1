<#
.SYNOPSIS
  Phase 2（Composite 第 1 群 + タスク対応）の統合検証（design.md §20 Phase 2 の完了条件）。

.DESCRIPTION
  実際の Visual Studio と samples/target を使い、複合コマンドが
  「調査 1 手」として役に立つかを確かめる。CI では動かせない。手で走らせる。

  検証用の作業ディレクトリを作り、そこに .stakeout.json を置いて実行する。
  応答の上限バイト数を小さくして、カーソル分割が実際に起きる状態を作るためである。

.EXAMPLE
  pwsh tests/integration/phase2.ps1 -VsPid 12345
#>
[CmdletBinding()]
param(
    [int]$VsPid = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stakeout = Join-Path $root 'src\Stakeout.Cli\bin\Debug\net8.0\stakeout.exe'

if (-not (Test-Path $stakeout)) { throw "stakeout.exe がありません。dotnet build を実行してください: $stakeout" }

$env:STAKEOUT_PIPE = "stakeout-phase2-$PID"

$script:pass = 0
$script:fail = 0
$script:harness = $null
$script:workDir = $null

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

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function StartHarness {
    param([string]$Bug, [string[]]$ExtraArgs = @())

    if ($script:harness -and -not $script:harness.HasExited) {
        Dbg detach | Out-Null
        $script:harness.Kill()
        $script:harness.WaitForExit(5000) | Out-Null
    }

    $dir = Join-Path $root "samples\target\build\x64-$Bug"
    $exe = Join-Path $dir 'Harness.exe'
    if (-not (Test-Path $exe)) { throw "samples/target/build.ps1 -Bug $Bug を実行してください" }

    $log = Join-Path $dir 'phase2-harness.log'
    $script:harness = Start-Process -FilePath $exe -ArgumentList (@('phase2', '--slow') + $ExtraArgs) `
        -WorkingDirectory $dir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"

    Start-Sleep -Seconds 2
    Assert (-not $script:harness.HasExited) "Harness ($Bug) がすぐに終了した"

    $attachArgs = @('attach', '--pid', $script:harness.Id)
    if ($VsPid -gt 0) { $attachArgs += @('--vs-pid', $VsPid) }
    DbgJson @attachArgs | Out-Null

    Write-Host "  harness pid=$($script:harness.Id) bug=$Bug"
}

try {
    # 応答の上限を小さくした作業ディレクトリを用意する。
    # カーソル分割は「大きすぎる応答」でしか起きないので、条件を作らないと検証できない
    $script:workDir = Join-Path ([System.IO.Path]::GetTempPath()) "stakeout-phase2-$PID"
    New-Item -ItemType Directory -Force -Path $script:workDir | Out-Null

    @'
{
  "backend": "envdte",
  "allowProcesses": ["^Harness\\.exe$"],
  "limits": { "responseBytes": 900 },
  "tasks": {
    "taskEntryPatterns": [ { "pattern": "^Task_(\\w+)_Main$", "name": "$1" } ]
  }
}
'@ | Set-Content -Path (Join-Path $script:workDir '.stakeout.json') -Encoding UTF8

    Push-Location $script:workDir

    Write-Host "work   : $script:workDir"
    Write-Host "pipe   : $($env:STAKEOUT_PIPE)"

    Step 'デーモンが検証用の設定を読み込む' {
        $status = DbgJson status
        Assert ($status.configPaths.Count -ge 1) '設定ファイルが読み込まれていない'
        Assert ($status.configPaths[0] -like "*$($script:workDir)*") `
            "作業ディレクトリの設定が使われていない: $($status.configPaths -join ', ')"
    }

    Step 'BUG_03: watch-until-change が書き込み元をタスク名付きで返す' {
        StartHarness -Bug BUG_03

        # データブレークポイントは停止中にしか張れない（ADR 0006）
        Dbg pause | Out-Null
        $paused = DbgJson wait --timeout 15
        Assert $paused.stopped '中断できなかった'

        # counter は NativeLib.dll のグローバル。どのフレームで止まっていても
        # 解決できるようモジュール修飾を付ける（ADR 0013）
        $result = DbgJson watch-until-change '{,,NativeLib.dll}g_shared.counter' --timeout 60 --max-hits 8

        Assert $result.changed '値の変化を捉えられなかった'
        Assert ($result.hits.Count -ge 1) '書き込みを 1 件も捉えていない'

        # design.md §9.2 の通り、値が変わった時点で止まる。
        # つまりここで捕まるのは最初の書き込み元であって、必ずしも「犯人」ではない。
        # 想定外の書き込みを名指しするのは find-corruption（Phase 4）の仕事である
        $hit = $result.hits[0]
        Assert ($hit.function -match 'nl_bump_counter|nl_stray_write') `
            "書き込み元が NativeLib の関数でない: $($hit.function)"

        Assert (-not [string]::IsNullOrEmpty($hit.taskName)) `
            "タスク名が付いていない (thread $($hit.threadId))"

        $stackFunctions = ($hit.stack | ForEach-Object { $_.function }) -join ' '
        Assert ($stackFunctions -match 'Task_._Main') "スタックにタスクのエントリが無い: $stackFunctions"

        Write-Host "  書き込み元: $($hit.function) task=$($hit.taskName) thread=$($hit.threadId)"
        Write-Host "  $($hit.before) -> $($hit.after)"
    }

    Step 'データブレークポイントは後始末される' {
        # 4 本しかない。使い切ると次の調査ができなくなる（ADR 0006）
        $list = @(DbgJson bp list)
        Assert ($list.Count -eq 0) "ブレークポイントが残っている: $($list.Count) 本"
    }

    Step 'BUG_04: run-until と dump が 1 手で原因を含む情報を返す' {
        StartHarness -Bug BUG_04

        $source = Join-Path $root 'samples\target\NativeLib\nativelib.c'
        $line = (Select-String -Path $source -Pattern 'g_ctx\.tick\+\+;' | Select-Object -First 1).LineNumber

        $result = DbgJson run-until "nativelib.c:$line" --cond 'g_ctx.state == 7' `
            --timeout 60 --expr 'g_ctx.state' --expr 'g_ctx.tick'

        Assert $result.reachedTarget '狙った位置に到達していない'
        Assert ($result.exprs.'g_ctx.state' -eq '7') "state が 7 でない: $($result.exprs.'g_ctx.state')"

        $functions = $result.stack | ForEach-Object { $_.function }
        Assert (($functions -join ' ') -match 'nl_update_state') "スタックに原因関数が無い: $($functions -join ', ')"
        Assert ($result.steps.Count -ge 3) '何をしたかが記録されていない'

        Write-Host "  state=$($result.exprs.'g_ctx.state') tick=$($result.exprs.'g_ctx.tick') at $($functions[0])"
    }

    Step 'dump が構造体を平らな経路付きで返す' {
        # 応答が小さく切ってあるので、続きを辿って全体を集める
        $nodes = @(DbgJson dump 'g_ctx' --depth 2 --max-items 8)
        while ($script:lastEnvelope.truncated) {
            $nodes += @(DbgJson dump 'g_ctx' --cursor $script:lastEnvelope.cursor)
        }

        $paths = $nodes | ForEach-Object { $_.path }
        Assert ($paths -contains 'g_ctx.state') "経路が付いていない: $($paths -join ', ')"
        Assert ($paths -contains 'g_ctx.inner.flags') "入れ子の経路が付いていない: $($paths -join ', ')"
        Assert ($paths -contains 'g_ctx.name[0]') "配列要素の経路が付いていない: $($paths -join ', ')"

        $root0 = $nodes | Where-Object { $_.depth -eq 0 } | Select-Object -First 1
        Assert ($root0.name -eq 'g_ctx') '根が g_ctx でない'

        Write-Host "  $($nodes.Count) 要素"
    }

    Step '大きい dump がカーソルで分割される' {
        # 作業ディレクトリの responseBytes を 900 に絞ってある
        $first = DbgJson dump 'g_ctx' --depth 3 --max-items 32
        Assert ($first.Count -ge 1) '1 件も返っていない'
        Assert ($script:lastEnvelope.truncated) '応答が上限に達していない（分割されていない）'

        $cursor = $script:lastEnvelope.cursor
        Assert (-not [string]::IsNullOrEmpty($cursor)) 'カーソルが返っていない'

        $rest = @(DbgJson dump 'g_ctx' --cursor $cursor)
        Assert ($rest.Count -ge 1) '続きが取れない'

        Write-Host "  1 ページ目 $($first.Count) 件 / 続き $($rest.Count) 件"
    }

    Step 'カーソルは 1 度しか使えない' {
        DbgJson dump 'g_ctx' --depth 3 --max-items 32 | Out-Null
        $cursor = $script:lastEnvelope.cursor
        Assert (-not [string]::IsNullOrEmpty($cursor)) 'カーソルが返っていない'

        DbgJson dump 'g_ctx' --cursor $cursor | Out-Null

        $second = Dbg dump 'g_ctx' --cursor $cursor --json
        Assert ($script:lastExit -ne 0) '使い終わったカーソルが再利用できてしまう'
        Assert ($second -match 'NOT_FOUND') "期限切れの扱いになっていない: $second"
    }

    Step 'task-map が全スレッドにタスク名を当てる' {
        $map = DbgJson task-map
        $named = @($map.tasks | Where-Object { $null -ne $_.taskName })

        Assert ($named.Count -ge 4) "タスク名が付いたスレッドが少ない: $($named.Count)"

        $names = ($named | ForEach-Object { $_.taskName } | Sort-Object) -join ','
        Assert ($names -match 'A' -and $names -match 'B' -and $names -match 'C' -and $names -match 'D') `
            "A/B/C/D が揃っていない: $names"

        Write-Host "  tasks: $names"
    }

    Step 'タスク名でスレッドを選べる' {
        $selected = DbgJson thread select --task C
        Assert ($selected.threadId -gt 0) 'タスク名からスレッドを解決できない'

        $frozen = DbgJson thread freeze --task C
        Assert ($frozen.frozen) '凍結できない'
        Assert ($frozen.threadId -eq $selected.threadId) '同じスレッドを指していない'

        $thawed = DbgJson thread freeze --task C --thaw
        Assert (-not $thawed.frozen) '凍結を解除できない'
    }

    Step '知らないタスク名は候補を挙げて断られる' {
        $text = Dbg thread select --task ZZZ
        Assert ($script:lastExit -eq 5) "終了コードが 5 (NOT_FOUND) でない: $script:lastExit"
        Assert ($text -match 'task-map') "hint に確認方法が無い: $text"
    }

    Step 'trace-expr がステップごとの値を並べる' {
        # 停止中である必要がある。直前の run-until で止まったまま
        # 現在のスレッドが NativeLib の外に居ることがある。
        # モジュール修飾を付ければどのフレームからでも読める（ADR 0013）
        $expr = '{,,NativeLib.dll}g_ctx.state'
        $trace = DbgJson trace-expr $expr --steps 5 --kind over
        Assert ($trace.points.Count -ge 1) 'ステップが記録されていない'

        $values = $trace.points | ForEach-Object { $_.values.$expr }
        Assert (($values -join '') -notmatch '評価できません') "式を評価できていない: $($values -join ', ')"
        Write-Host "  state: $($values -join ' -> ')"
    }
}
finally {
    if ($script:workDir -and (Test-Path $script:workDir)) {
        Dbg detach | Out-Null
        Dbg daemon stop | Out-Null
        Pop-Location
        Remove-Item -Recurse -Force $script:workDir -ErrorAction SilentlyContinue
    }

    if ($script:harness -and -not $script:harness.HasExited) {
        $script:harness.Kill()
    }

    Write-Host ""
    Write-Host "pass=$script:pass fail=$script:fail" -ForegroundColor ($(if ($script:fail -eq 0) { 'Green' } else { 'Red' }))
}

exit ($(if ($script:fail -eq 0) { 0 } else { 1 }))
