<#
.SYNOPSIS
  評価スイート（design.md §18.3）。エージェントが Skill だけを頼りに原因を当てられるかを測る。

.DESCRIPTION
  各ケースについて、答えの手がかりを消したソースの写しを一時ディレクトリに作り、
  そこからビルドした Harness を起動して、症状の文だけを Claude Code に渡して調査させる。
  出力に正解の語が含まれているかで判定し、所要時間と呼んだコマンド数を記録する。

  **エージェントにはリポジトリを見せない（ADR 0022）。** サンプルのソースには原因が
  コメントで書いてあり、docs や eval/results には正解の関数名がある。
  作業ディレクトリ・ソース・PDB・stakeout 本体をすべてリポジトリの外に置く。
  それでもリポジトリを読みに行った測定は、PASS でも無効（INVALID）にする。

  **このスクリプトは Claude Code を起動する。API の利用料がかかる。**
  ケース 1 件あたり数十ターン、数分かかることがある。

.PARAMETER Case
  実行するケース（BUG_01 / BUG_03 / BUG_04 / BUG_05）。省略時は全部。

.PARAMETER VsPid
  使う Visual Studio の pid。

.PARAMETER MaxTurns
  1 ケースあたりの上限ターン数。

.PARAMETER PrepareOnly
  写しの生成・ビルド・漏れの検査・stakeout から Harness が見えることの確認までを行い、
  Claude Code は起動しない。Visual Studio も API も要らない。
  評価の仕組みを変えたら、まずこれで確かめる。

.PARAMETER KeepWorkspace
  終わっても一時ディレクトリを消さない。エージェントに何が見えていたかを確かめるときに使う。

.EXAMPLE
  pwsh eval/run.ps1 -PrepareOnly
  pwsh eval/run.ps1 -Case BUG_04 -VsPid 12345
#>
[CmdletBinding()]
param(
    [string[]]$Case = @('BUG_01', 'BUG_03', 'BUG_04', 'BUG_05'),
    [int]$VsPid = 0,
    [int]$MaxTurns = 40,
    [switch]$PrepareOnly,
    [switch]$KeepWorkspace
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# パイプに流す文字コード。既定（CP932）のままだと、症状の日本語が化けたまま
# エージェントに届く。届いていないことに気づかないまま「解けた」と記録してしまう
$OutputEncoding = New-Object System.Text.UTF8Encoding $false
$utf8 = New-Object System.Text.UTF8Encoding $false

$root = Split-Path -Parent $PSScriptRoot
$cliBin = Join-Path $root 'src\Stakeout.Cli\bin\Debug\net8.0'
$daemonBin = Join-Path $root 'src\Stakeout.Daemon\bin\Debug\net8.0-windows'
$skill = Join-Path $root 'skills\stakeout\SKILL.md'

. (Join-Path $PSScriptRoot 'sanitize.ps1')

if (-not (Test-Path (Join-Path $cliBin 'stakeout.exe'))) { throw "stakeout.exe がありません。dotnet build を実行してください: $cliBin" }
if (-not (Test-Path (Join-Path $daemonBin 'stakeoutd.exe'))) { throw "stakeoutd.exe がありません。dotnet build を実行してください: $daemonBin" }
if (-not (Test-Path $skill)) { throw "SKILL.md がありません: $skill" }
if (-not $PrepareOnly -and -not (Get-Command claude -ErrorAction SilentlyContinue)) {
    throw 'claude コマンドが見つかりません。Claude Code をインストールしてください。'
}
if (-not (Get-Command gtags -ErrorAction SilentlyContinue)) {
    Write-Warning 'gtags が見つかりません。エージェントは stakeout code / find-corruption を使えません'
}

$env:STAKEOUT_PIPE = "stakeout-eval-$PID"

$casesDir = Join-Path $PSScriptRoot 'cases'
$hiddenCases = Join-Path ([System.IO.Path]::GetTempPath()) "stakeout-eval-cases-$PID"

$results = @()

function ParseCase {
    param([string]$Name)

    $path = Join-Path $casesDir "$Name.md"
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
  エージェントに見せる一式をリポジトリの外に作る。

  ディレクトリ名にケース名を入れない。パスはスタックやプロセス一覧からエージェントに見える。
#>
function New-Workspace {
    param($Spec)

    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $temp = [System.IO.Path]::GetTempPath()
    $work = Join-Path $temp "stakeout-eval-$id"
    $tools = Join-Path $temp "stakeout-eval-$id-tools"
    $target = Join-Path $work 'target'
    $bin = Join-Path $target 'bin'

    $ws = [pscustomobject]@{
        Root     = $work
        Tools    = $tools
        Target   = $target
        Bin      = $bin
        Harness  = Join-Path $bin 'Harness.exe'
        Stakeout = Join-Path $tools 'cli\stakeout.exe'
        Daemon   = Join-Path $tools 'daemon\stakeoutd.exe'
    }

    # 1. ソースの写し。答えを消し、消せていることを確かめる
    foreach ($rel in 'NativeLib\nativelib.c', 'NativeLib\nativelib.h', 'Harness\harness.c') {
        $text = [System.IO.File]::ReadAllText((Join-Path $root "samples\target\$rel"), [System.Text.Encoding]::UTF8)
        $clean = ConvertTo-EvalSource -Text $text -Bug $Spec.Name

        $before = ($text -split "\r?\n").Count
        $after = ($clean -split "\r?\n").Count
        if ($before -ne $after) {
            throw "$rel の行数が変わりました（$before -> $after）。ブレークポイントの行が PDB とずれます"
        }

        $leaks = @(Find-EvalLeaks $clean)
        if ($leaks.Count -gt 0) {
            throw "$rel に答えの手がかりが残っています。測定を始めません:`n$($leaks -join "`n")"
        }

        $dest = Join-Path $target $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        [System.IO.File]::WriteAllText($dest, $clean, $utf8)
    }

    # 2. 写しからビルドする。-Bug は渡さない。バグの枝は写しの中で無条件のコードになっており、
    #    /DBUG_NN を渡すとコンパイラの引数として PDB に残る
    $shell = (Get-Process -Id $PID).Path
    & $shell -NoProfile -File (Join-Path $root 'samples\target\build.ps1') -SourceRoot $target -OutDir $bin | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "写しのビルドに失敗しました（$LASTEXITCODE）" }

    # PDB と実行ファイルにリポジトリのパスが残っていれば、エージェントはそこから辿れる
    foreach ($file in Get-ChildItem $bin -Include *.exe, *.dll, *.pdb -Recurse) {
        $content = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($file.FullName))
        if ($content.IndexOf($root, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "$($file.Name) にリポジトリのパスが残っています"
        }
    }

    # 3. 索引。gtagsRoot は必ず写しに向ける。
    #    向けないと、ユーザー設定（%APPDATA%）の gtagsRoot がリポジトリを指していた場合にそれが使われる
    if (Get-Command gtags -ErrorAction SilentlyContinue) {
        Push-Location $target
        try { & gtags | Out-Null } finally { Pop-Location }
    }

    $config = [ordered]@{
        backend        = 'envdte'
        allowProcesses = @('^Harness\.exe$')
        allowLaunch    = $false
        code           = @{ gtagsRoot = $target }
        tasks          = @{ taskEntryPatterns = @(@{ pattern = '^Task_(\w+)_Main$'; name = '$1' }) }
    }
    [System.IO.File]::WriteAllText((Join-Path $work '.stakeout.json'), ($config | ConvertTo-Json -Depth 5), $utf8)

    # 4. stakeout 本体。PDB は持ち出さない。例外のスタックトレースにリポジトリのパスが出る。
    #    CLI（net8.0）とデーモン（net8.0-windows）は依存の組が違うので、同じディレクトリに混ぜない。
    #    デーモンの場所は STAKEOUT_EXE で渡す
    foreach ($pair in @(@($cliBin, 'cli'), @($daemonBin, 'daemon'))) {
        $dest = Join-Path $tools $pair[1]
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item -Path (Join-Path $pair[0] '*') -Destination $dest -Recurse -Force -Exclude *.pdb
    }

    return $ws
}

function Remove-Workspace {
    param($Ws)

    if ($KeepWorkspace) {
        Write-Host "  workspace: $($Ws.Root)"
        return
    }

    # デーモンと Harness が落ち切るまで、ファイルが掴まれていることがある
    foreach ($dir in $Ws.Root, $Ws.Tools) {
        for ($i = 0; $i -lt 20 -and (Test-Path $dir); $i++) {
            try { Remove-Item -Recurse -Force $dir -ErrorAction Stop }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (Test-Path $dir) { Write-Warning "消せませんでした: $dir" }
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

<# パスの表記を揃える。JSON のエスケープ、スラッシュ、大文字小文字の違いを吸収する。 #>
function NormalizePath {
    param([string]$Text)
    return (($Text -replace '\\\\', '\') -replace '/', '\').ToLowerInvariant()
}

<#
  エージェントがリポジトリや退避した正解を読みに行ったツール呼び出しを返す。
  Git Bash の /c/Users/... や WSL の /mnt/c/... の表記も拾う。
#>
function Find-ForbiddenAccess {
    param([object[]]$Events)

    $forbidden = @(
        (NormalizePath $root),
        (NormalizePath ($root -replace '^([A-Za-z]):', '/$1')),
        (NormalizePath $hiddenCases)
    )

    foreach ($evt in $Events) {
        if ($evt.type -ne 'assistant') { continue }

        foreach ($part in @($evt.message.content)) {
            if ($part.type -ne 'tool_use') { continue }

            $toolInput = $part.input | ConvertTo-Json -Depth 10 -Compress
            $normalized = NormalizePath $toolInput

            if ($forbidden | Where-Object { $normalized.Contains($_) }) {
                $shown = if ($toolInput.Length -gt 200) { $toolInput.Substring(0, 200) + '...' } else { $toolInput }
                "$($part.name): $shown"
            }
        }
    }
}

<# -PrepareOnly: エージェントを起動する直前までが整っていることを確かめる。 #>
function Test-Workspace {
    param($Ws, $Spec, $Harness)

    $out = & $Ws.Stakeout targets --json 2>&1 | Out-String
    $seen = ($LASTEXITCODE -eq 0) -and ($out -match "`"pid`"\s*:\s*$($Harness.Id)\b")

    if ($seen) {
        Write-Host "  READY 写し: $($Ws.Target) / Harness pid $($Harness.Id) が stakeout から見える" -ForegroundColor Green
    }
    else {
        Write-Host "  BROKEN stakeout targets に Harness (pid $($Harness.Id)) が出ない" -ForegroundColor Red
        Write-Host $out
    }

    return [pscustomobject]@{
        Case     = $Spec.Name
        Status   = $(if ($seen) { 'READY' } else { 'BROKEN' })
        Seconds  = 0
        Commands = 0
        Turns    = 0
        Matched  = ''
        Note     = ''
    }
}

function RunCase {
    param($Spec)

    Write-Host ""
    Write-Host "=== $($Spec.Name) ===" -ForegroundColor Cyan

    $ws = New-Workspace $Spec
    $harness = $null
    $previousDaemon = $env:STAKEOUT_EXE
    $env:STAKEOUT_EXE = $ws.Daemon

    # stakeout とデーモンは作業ディレクトリの .stakeout.json を読む。エージェントもここで起動する
    Push-Location $ws.Root

    try {
        $log = Join-Path $ws.Bin 'harness.log'
        $harness = Start-Process -FilePath $ws.Harness -ArgumentList $Spec.HarnessArgs `
            -WorkingDirectory $ws.Bin -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"

        Start-Sleep -Seconds 2

        if ($PrepareOnly) {
            return Test-Workspace $ws $Spec $harness
        }

        # 作業ツリーの外に出していても、正解ファイルは退避する。隠せるものは隠す（ADR 0016）
        Move-Item -Path $casesDir -Destination $hiddenCases -Force

        # 調査対象の pid だけを伝える。どう調べるかは Skill に任せる
        $vsHint = if ($VsPid -gt 0) { " Visual Studio の pid は $VsPid です（stakeout attach --vs-pid で指定してください）。" } else { '' }

        $prompt = @"
$($Spec.Symptom)

調査対象のプロセス ID は $($harness.Id) です。$vsHint
ソースコードは ./target にあります。stakeout は $($ws.Stakeout) です。
コードを読んで仮説を立て、stakeout で実測して確かめ、原因を file:line と関数名で報告してください。
コマンドには常に --json を付けてください。結果は .data に入っています。
"@

        $resultsDir = Join-Path $PSScriptRoot 'results'
        New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

        # 生の記録は .jsonl / .txt で残す。.gitignore が追跡しない（リポジトリに載せない）
        $transcript = Join-Path $resultsDir "$($Spec.Name)-transcript.jsonl"
        $stderrLog = Join-Path $resultsDir "$($Spec.Name)-stderr.txt"
        $finalLog = Join-Path $resultsDir "$($Spec.Name)-final.txt"

        # 呼び出し回数はデーモンのログから数える。
        # エージェントの最終出力にはコマンドが残らないので、そこから数えても 0 になる
        $logDir = Join-Path $env:LOCALAPPDATA 'stakeout\logs'
        $rpcBefore = CountRpc $logDir

        $sw = [System.Diagnostics.Stopwatch]::StartNew()

        # Skill をシステムプロンプトとして渡す。エージェントは Skill だけを頼りに調べる
        $skillText = [System.IO.File]::ReadAllText($skill, [System.Text.Encoding]::UTF8)

        # stream-json にするのは、最終出力だけでなくツール呼び出しをすべて残すためである。
        # 何を読んだかが分からなければ、答えを読んだかどうかも分からない
        $lines = $prompt | & claude -p `
            --output-format stream-json --verbose `
            --append-system-prompt $skillText `
            --max-turns $MaxTurns `
            --allowedTools Bash Read Grep Glob 2> $stderrLog

        $sw.Stop()
        [System.IO.File]::WriteAllLines($transcript, [string[]]@($lines), $utf8)

        $events = foreach ($line in @($lines)) {
            try { $line | ConvertFrom-Json -ErrorAction Stop } catch { }
        }

        $final = @($events | Where-Object { $_.type -eq 'result' }) | Select-Object -Last 1
        $output = if ($final) { [string]$final.result } else { '' }
        $turns = if ($final) { $final.num_turns } else { 0 }
        [System.IO.File]::WriteAllText($finalLog, $output, $utf8)

        $commands = (CountRpc $logDir) - $rpcBefore
        $hit = @($Spec.Expected | Where-Object { $output -match [regex]::Escape($_) })
        $forbidden = @(Find-ForbiddenAccess $events)

        # 判定の順序: 無効 → 合否。無効な測定を PASS として数えない
        $note = ''
        if ($output -match '文字化け|garbled|mojibake') {
            $status = 'INVALID'
            $note = '症状の文が化けて届いている'
        }
        elseif ($forbidden.Count -gt 0) {
            $status = 'INVALID'
            $note = "リポジトリを読みに行った: $($forbidden[0])"
        }
        elseif (-not $final) {
            $status = 'FAIL'
            $note = "Claude Code の結果が無い（$stderrLog を見る）"
        }
        elseif ($hit.Count -ge 2) {
            $status = 'PASS'
        }
        else {
            $status = 'FAIL'
            if ($final.subtype -ne 'success') { $note = $final.subtype }
        }

        $color = switch ($status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
        Write-Host ("  {0} {1:F0} 秒 / {2} ターン / stakeout 呼び出し {3} 回 / 一致 {4} {5}" -f `
            $status, $sw.Elapsed.TotalSeconds, $turns, $commands, ($hit -join ','), $note) -ForegroundColor $color

        return [pscustomobject]@{
            Case     = $Spec.Name
            Status   = $status
            Seconds  = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
            Commands = $commands
            Turns    = $turns
            Matched  = ($hit -join ',')
            Note     = $note
        }
    }
    finally {
        Pop-Location
        if (Test-Path $hiddenCases) { Move-Item -Path $hiddenCases -Destination $casesDir -Force }

        & $ws.Stakeout detach --json 2>&1 | Out-Null
        & $ws.Stakeout daemon stop --json 2>&1 | Out-Null
        if ($harness -and -not $harness.HasExited) {
            $harness.Kill()
            $harness.WaitForExit(5000) | Out-Null
        }

        $env:STAKEOUT_EXE = $previousDaemon
        Remove-Workspace $ws
    }
}

# ケースは退避する前に全部読んでおく
$specs = @($Case | ForEach-Object { ParseCase $_ })

try {
    foreach ($spec in $specs) {
        $results += RunCase $spec
    }
}
finally {
    if (-not $PrepareOnly -and $results.Count -gt 0) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $reportPath = Join-Path $PSScriptRoot "results\$stamp.md"
        New-Item -ItemType Directory -Force -Path (Split-Path $reportPath) | Out-Null

        $lines = @(
            "# 評価結果 $stamp",
            '',
            'エージェントにはリポジトリを見せず、答えを消したソースの写しだけを渡した（ADR 0022）。',
            '',
            '| ケース | 判定 | 所要秒 | ターン | stakeout 呼び出し | 一致した語 | 備考 |',
            '|---|---|---|---|---|---|---|'
        )

        foreach ($r in $results) {
            $lines += "| $($r.Case) | $($r.Status) | $($r.Seconds) | $($r.Turns) | $($r.Commands) | $($r.Matched) | $($r.Note) |"
        }

        $lines += ''
        $lines += "合格 $(@($results | Where-Object { $_.Status -eq 'PASS' }).Count) / $($results.Count)" +
            "（無効 $(@($results | Where-Object { $_.Status -eq 'INVALID' }).Count)）"

        [System.IO.File]::WriteAllText($reportPath, ($lines -join "`n"), $utf8)
        Write-Host ""
        Write-Host "結果: $reportPath"
    }
}

$ok = if ($PrepareOnly) { 'READY' } else { 'PASS' }
exit ($(if (@($results | Where-Object { $_.Status -ne $ok }).Count -eq 0) { 0 } else { 1 }))
