<#
.SYNOPSIS
  評価スイート（design.md §18.3）。エージェントが Skill だけを頼りに原因を当てられるかを測る。

.DESCRIPTION
  各ケースについて Harness を起動し、症状の文だけを Claude Code に渡して調査させる。
  出力に正解の語が含まれているかで判定し、所要時間と呼んだコマンド数を記録する。

  **このスクリプトは Claude Code を起動する。API の利用料がかかる。**
  ケース 1 件あたり数十ターン、数分かかることがある。

.PARAMETER Case
  実行するケース（BUG_01 / BUG_03 / BUG_04 / BUG_05）。省略時は全部。

.PARAMETER VsPid
  使う Visual Studio の pid。

.PARAMETER MaxTurns
  1 ケースあたりの上限ターン数。

.EXAMPLE
  pwsh eval/run.ps1 -Case BUG_04 -VsPid 12345
#>
[CmdletBinding()]
param(
    [string[]]$Case = @('BUG_01', 'BUG_03', 'BUG_04', 'BUG_05'),
    [int]$VsPid = 0,
    [int]$MaxTurns = 40
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# パイプに流す文字コード。既定（CP932）のままだと、症状の日本語が化けたまま
# エージェントに届く。届いていないことに気づかないまま「解けた」と記録してしまう
$OutputEncoding = New-Object System.Text.UTF8Encoding $false

$root = Split-Path -Parent $PSScriptRoot
$stakeout = Join-Path $root 'src\Stakeout.Cli\bin\Debug\net8.0\stakeout.exe'
$skill = Join-Path $root 'skills\stakeout\SKILL.md'

if (-not (Test-Path $stakeout)) { throw "stakeout.exe がありません: $stakeout" }
if (-not (Test-Path $skill)) { throw "SKILL.md がありません: $skill" }
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    throw 'claude コマンドが見つかりません。Claude Code をインストールしてください。'
}

$env:STAKEOUT_PIPE = "stakeout-eval-$PID"
$env:DBG_JSON = '1'

$results = @()

function ParseCase {
    param([string]$Name)

    $path = Join-Path $PSScriptRoot "cases\$Name.md"
    if (-not (Test-Path $path)) { throw "ケースがありません: $path" }

    # Windows PowerShell の Get-Content は BOM 無し UTF-8 を CP932 として読む。
    # ケースの日本語が化けると、症状も判定語も取り出せなくなる
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)

    # 症状は「## 症状」の見出しから次の見出しまで
    $symptom = [regex]::Match($text, '(?s)## 症状[^\r\n]*\r?\n(.*?)\r?\n## ').Groups[1].Value.Trim()
    $answer = [regex]::Match($text, '(?s)## 判定\r?\n(.*?)\r?\n## ').Groups[1].Value.Trim()

    # 判定に使う語は「正解」節の関数名から拾う
    $expected = [regex]::Matches($text, '`(nl_\w+|Task_\w+|strcpy|nativelib\.c)`') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

    $harnessArgs = [regex]::Match($text, '起動: `Harness\.exe ([^`]*)`').Groups[1].Value

    if (-not $symptom) { throw "$Name の症状を読み取れません（文字コードを確認してください）" }
    if (-not $harnessArgs) { throw "$Name の起動引数を読み取れません" }

    return [pscustomobject]@{
        Name        = $Name
        Symptom     = $symptom
        Criteria    = $answer
        Expected    = $expected
        HarnessArgs = ($harnessArgs -split '\s+' | Where-Object { $_ })
    }
}

<#
  デーモンのログに残った RPC 要求の数を数える。
  エージェントが実際に何回この道具を呼んだかを測る、唯一の確かな手段である。
#>
function CountRpc {
    param([string]$LogDir)

    if (-not (Test-Path $LogDir)) { return 0 }

    $count = 0
    foreach ($file in Get-ChildItem $LogDir -Filter '*.jsonl' -ErrorAction SilentlyContinue) {
        $stream = [System.IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
        try {
            $reader = New-Object System.IO.StreamReader($stream)
            $count += ([regex]::Matches($reader.ReadToEnd(), '"kind":"rpc\.request"')).Count
        }
        finally {
            $stream.Dispose()
        }
    }

    return $count
}

function RunCase {
    param($Spec)

    Write-Host ""
    Write-Host "=== $($Spec.Name) ===" -ForegroundColor Cyan

    $dir = Join-Path $root "samples\target\build\x64-$($Spec.Name)"
    $exe = Join-Path $dir 'Harness.exe'
    if (-not (Test-Path $exe)) { throw "samples/target/build.ps1 -Bug $($Spec.Name) を実行してください" }

    $log = Join-Path $dir 'eval-harness.log'
    $harness = Start-Process -FilePath $exe -ArgumentList $Spec.HarnessArgs `
        -WorkingDirectory $dir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"

    Start-Sleep -Seconds 2

    # 正解ファイルを一時的に外へ出す。同じリポジトリに置いたままでは、
    # エージェントがそれを読んでしまい、測っているのが「調査能力」ではなく
    # 「答えの探し方」になる（実際に一度そうなった）
    $casesDir = Join-Path $PSScriptRoot 'cases'
    $hidden = Join-Path ([System.IO.Path]::GetTempPath()) "stakeout-eval-cases-$PID"
    Move-Item -Path $casesDir -Destination $hidden -Force

    try {
        # 調査対象の pid だけを伝える。どう調べるかは Skill に任せる
        $vsHint = if ($VsPid -gt 0) { " Visual Studio の pid は $VsPid です（stakeout attach --vs-pid で指定してください）。" } else { '' }

        $prompt = @"
$($Spec.Symptom)

調査対象のプロセス ID は $($harness.Id) です。$vsHint
$stakeout を使って調べ、原因を file:line と関数名で報告してください。
コマンドには常に --json を付けてください。結果は .data に入っています。

ソースコードは読んで構いませんが、eval/ 配下（検証用の想定解が置かれています）は見ないでください。
デバッガの実測から原因を突き止めてください。
"@

        $transcript = Join-Path $PSScriptRoot "results\$($Spec.Name)-transcript.txt"
        New-Item -ItemType Directory -Force -Path (Split-Path $transcript) | Out-Null

        # 呼び出し回数はデーモンのログから数える。
        # エージェントの最終出力にはコマンドが残らないので、そこから数えても 0 になる
        $logDir = Join-Path $env:LOCALAPPDATA 'stakeout\\logs'
        $rpcBefore = CountRpc $logDir

        $sw = [System.Diagnostics.Stopwatch]::StartNew()

        # Skill をシステムプロンプトとして渡す。エージェントは Skill だけを頼りに調べる
        $skillText = [System.IO.File]::ReadAllText($skill, [System.Text.Encoding]::UTF8)

        $output = $prompt | & claude -p `
            --append-system-prompt $skillText `
            --allowedTools 'Bash' `
            --max-turns $MaxTurns 2>&1 | Out-String

        $sw.Stop()
        $output | Set-Content -Path $transcript -Encoding UTF8

        if ($output -match '文字化け|garbled|mojibake') {
            throw '症状の文が化けて届いている。この結果は無効なので、文字コードを直してから測り直すこと'
        }

        $hit = @($Spec.Expected | Where-Object { $output -match [regex]::Escape($_) })
        $correct = $hit.Count -ge 2

        $commands = (CountRpc $logDir) - $rpcBefore

        Write-Host ("  {0} {1:F0} 秒 / stakeout 呼び出し {2} 回 / 一致 {3}" -f `
            $(if ($correct) { 'PASS' } else { 'FAIL' }), $sw.Elapsed.TotalSeconds, $commands, ($hit -join ','))

        return [pscustomobject]@{
            Case      = $Spec.Name
            Correct   = $correct
            Seconds   = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
            Commands  = $commands
            Matched   = ($hit -join ',')
            Expected  = ($Spec.Expected -join ',')
        }
    }
    finally {
        if (Test-Path $hidden) { Move-Item -Path $hidden -Destination $casesDir -Force }

        & $stakeout detach --json 2>&1 | Out-Null
        if (-not $harness.HasExited) { $harness.Kill() }
    }
}

try {
    foreach ($name in $Case) {
        $results += RunCase (ParseCase $name)
    }
}
finally {
    & $stakeout daemon stop --json 2>&1 | Out-Null

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $reportPath = Join-Path $PSScriptRoot "results\$stamp.md"
    New-Item -ItemType Directory -Force -Path (Split-Path $reportPath) | Out-Null

    $lines = @(
        "# 評価結果 $stamp",
        '',
        '| ケース | 判定 | 所要秒 | stakeout 呼び出し | 一致した語 |',
        '|---|---|---|---|---|'
    )

    foreach ($r in $results) {
        $lines += "| $($r.Case) | $(if ($r.Correct) { 'PASS' } else { 'FAIL' }) | $($r.Seconds) | $($r.Commands) | $($r.Matched) |"
    }

    $lines += ''
    $lines += "合格 $(@($results | Where-Object { $_.Correct }).Count) / $($results.Count)"

    $lines -join "`n" | Set-Content -Path $reportPath -Encoding UTF8
    Write-Host ""
    Write-Host "結果: $reportPath"
}

exit ($(if (@($results | Where-Object { -not $_.Correct }).Count -eq 0) { 0 } else { 1 }))
