<#
.SYNOPSIS
  Phase 4（Code Index と find-corruption）の統合検証（design.md §20 Phase 4 の完了条件）。

.DESCRIPTION
  gtags の索引と、それを使った find-corruption を確かめる。
  `code *` はデバッガに依存しないので、Target 無しでも一部は動く。

.EXAMPLE
  pwsh tests/integration/phase4.ps1 -VsPid 12345
#>
[CmdletBinding()]
param(
    [int]$VsPid = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stakeout = Join-Path $root 'src\Stakeout.Cli\bin\Debug\net8.0\stakeout.exe'
$targetRoot = Join-Path $root 'samples\target'

if (-not (Test-Path $stakeout)) { throw "stakeout.exe がありません: $stakeout" }
if (-not (Test-Path (Join-Path $targetRoot 'GTAGS'))) {
    throw "GTAGS がありません。$targetRoot で gtags を実行してください"
}

$env:STAKEOUT_PIPE = "stakeout-phase4-$PID"

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

    $script:lastEnvelope = $text | ConvertFrom-Json
    return $script:lastEnvelope.data
}

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    # gtags のルートを指した設定を作る。相対パスだと作業ディレクトリに依存する
    $script:workDir = Join-Path ([System.IO.Path]::GetTempPath()) "stakeout-phase4-$PID"
    New-Item -ItemType Directory -Force -Path $script:workDir | Out-Null

    $escapedRoot = $targetRoot -replace '\\', '\\\\'
    @"
{
  "backend": "envdte",
  "allowProcesses": ["^Harness\\.exe$"],
  "code": { "gtagsRoot": "$escapedRoot" },
  "tasks": {
    "taskEntryPatterns": [ { "pattern": "^Task_(\\w+)_Main$", "name": "`$1" } ]
  }
}
"@ | Set-Content -Path (Join-Path $script:workDir '.stakeout.json') -Encoding UTF8

    Push-Location $script:workDir
    Write-Host "work   : $script:workDir"

    Step 'code def が関数の定義を返す' {
        $defs = @(DbgJson code def nl_stray_write)
        Assert ($defs.Count -ge 1) '定義が見つからない'
        Assert ($defs[0].file -match 'nativelib\.c') "定義のファイルが違う: $($defs[0].file)"
    }

    Step 'code writers が代入行を列挙し、確信度を付ける' {
        $sites = @(DbgJson code writers 'g_shared.counter')
        Assert ($sites.Count -ge 2) "候補が少なすぎる: $($sites.Count)"

        $high = @($sites | Where-Object { $_.confidence -eq 'high' })
        Assert ($high.Count -ge 1) '確信度 high の候補が無い'

        $bump = @($sites | Where-Object { $_.function -eq 'nl_bump_counter' })
        Assert ($bump.Count -eq 1) "正規の書き込み元が候補に無い: $(($sites | ForEach-Object { $_.function }) -join ', ')"
        Assert ($bump[0].confidence -eq 'high') "確信度が high でない: $($bump[0].confidence)"

        $low = @($sites | Where-Object { $_.confidence -eq 'low' })
        Assert ($low.Count -ge 1) 'アドレス渡しが low として拾えていない'

        Write-Host "  候補 $($sites.Count) 件（high $($high.Count) / low $($low.Count)）"
    }

    Step 'モジュール修飾を付けても候補が出る' {
        # デバッガ向けの記法。ソースには無いので、照合前に落とす必要がある
        $sites = @(DbgJson code writers '{,,NativeLib.dll}g_shared.counter')
        $bump = @($sites | Where-Object { $_.function -eq 'nl_bump_counter' })
        Assert ($bump.Count -eq 1) '修飾付きだと候補が出ない'
    }

    Step '読み取り行は候補にならない' {
        $sites = @(DbgJson code writers 'g_ctx.state')

        # printf の書式文字列や比較は書き込みではない
        $bogus = @($sites | Where-Object { $_.text -match 'printf' })
        Assert ($bogus.Count -eq 0) "書式文字列を候補にしている: $($bogus | ForEach-Object { $_.text })"
    }

    Step '索引が無いシンボルでも落ちない' {
        $sites = @(DbgJson code writers 'g_does_not_exist')
        Assert ($sites.Count -eq 0) '存在しないシンボルに候補が出た'
    }

    Step 'BUG_03: find-corruption が候補に無い書き込みだけを名指しする' {
        $dir = Join-Path $root 'samples\target\build\x64-BUG_03'
        $exe = Join-Path $dir 'Harness.exe'
        Assert (Test-Path $exe) "samples/target/build.ps1 -Bug BUG_03 を実行してください"

        $log = Join-Path $dir 'phase4-harness.log'
        $script:harness = Start-Process -FilePath $exe -ArgumentList 'phase4', '--slow' `
            -WorkingDirectory $dir -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"

        Start-Sleep -Seconds 2

        $attachArgs = @('attach', '--pid', $script:harness.Id)
        if ($VsPid -gt 0) { $attachArgs += @('--vs-pid', $VsPid) }
        DbgJson @attachArgs | Out-Null

        Dbg pause | Out-Null
        $paused = DbgJson wait --timeout 15
        Assert $paused.stopped '中断できなかった'

        $result = DbgJson find-corruption '{,,NativeLib.dll}g_shared.counter' --max-hits 6 --timeout 90

        Assert $result.indexAvailable 'Code Index が使えていない'
        Assert ($result.hits.Count -ge 2) "書き込みが少なすぎる: $($result.hits.Count)"

        # 正規の書き込み元は候補に一致し、範囲外書き込みは一致しない。
        # この区別ができることが find-corruption の存在理由である
        $unexpected = @($result.hits | Where-Object { -not $_.expected })
        Assert ($unexpected.Count -ge 1) '候補に無い書き込みを 1 件も検出していない'

        $stray = @($unexpected | Where-Object { $_.function -like '*nl_stray_write*' })
        Assert ($stray.Count -ge 1) `
            "範囲外書き込みを名指しできていない: $(($unexpected | ForEach-Object { $_.function }) -join ', ')"

        Assert ($stray[0].taskName -eq 'C') "タスク名が付いていない: '$($stray[0].taskName)'"

        $bump = @($result.hits | Where-Object { $_.function -like '*nl_bump_counter*' })
        Assert ($bump.Count -ge 1) '正規の書き込みを捉えていない'
        Assert ($bump[0].expected) '正規の書き込みを候補外と誤判定している'

        Write-Host "  想定外: $($stray[0].function) task=$($stray[0].taskName) $($stray[0].before) -> $($stray[0].after)"
        Write-Host "  想定内: $($bump[0].function)（$($bump[0].note)）"
    }

    Step 'データブレークポイントが後始末されている' {
        $list = @(DbgJson bp list)
        Assert ($list.Count -eq 0) "ブレークポイントが残っている: $($list.Count) 本"
    }

    Step 'Code Index が未設定なら理由を言って断る' {
        # 設定を外した作業ディレクトリで、別のデーモンを起こす
        $bare = Join-Path ([System.IO.Path]::GetTempPath()) "stakeout-phase4-bare-$PID"
        New-Item -ItemType Directory -Force -Path $bare | Out-Null
        '{ "backend": "envdte", "allowProcesses": ["^Harness\\.exe$"] }' |
            Set-Content -Path (Join-Path $bare '.stakeout.json') -Encoding UTF8

        $previousPipe = $env:STAKEOUT_PIPE
        $env:STAKEOUT_PIPE = "stakeout-phase4-bare-$PID"
        Push-Location $bare
        try {
            Dbg status | Out-Null
            $text = Dbg code writers g_shared.counter
            Assert ($script:lastExit -ne 0) '未設定でも成功してしまう'
            Assert ($text -match 'NOT_CONFIGURED') "NOT_CONFIGURED になっていない: $text"
            Assert ($text -match 'gtagsRoot') "hint に設定キーが無い: $text"
            Dbg daemon stop | Out-Null
        }
        finally {
            Pop-Location
            $env:STAKEOUT_PIPE = $previousPipe
            Remove-Item -Recurse -Force $bare -ErrorAction SilentlyContinue
        }
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
