<#
.SYNOPSIS
  Phase 7（MCP アダプタ）の統合検証（design.md §20 Phase 7 の完了条件）。

.DESCRIPTION
  stakeout-mcp を stdio で起動し、MCP クライアントと同じ手順で会話する。
  initialize -> tools/list -> tools/call（attach -> run-until -> dump -> detach）。

  Claude Desktop そのものは使わない。**同じワイヤプロトコルを話す**ところまでを確かめる。
  実クライアントでの確認は、この検証が通ったうえで別途行うこと。

.EXAMPLE
  pwsh tests/integration/phase7.ps1 -VsPid 12345
#>
[CmdletBinding()]
param(
    [int]$VsPid = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$mcp = Join-Path $root 'src\Stakeout.Mcp\bin\Debug\net8.0\stakeout-mcp.exe'
$stakeout = Join-Path $root 'src\Stakeout.Cli\bin\Debug\net8.0\stakeout.exe'
$source = Join-Path $root 'samples\target\NativeLib\nativelib.c'

if (-not (Test-Path $mcp)) { throw "stakeout-mcp.exe がありません: $mcp" }

$env:STAKEOUT_PIPE = "stakeout-phase7-$PID"

$script:pass = 0
$script:fail = 0
$script:harness = $null

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

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

<#
  1 回の起動で会話を流し込み、応答をまとめて受け取る。
  MCP は 1 行 1 メッセージの JSON なので、この形で十分に実物と同じ経路を通る。
#>
function Converse {
    param([object[]]$Messages)

    $input = Join-Path ([System.IO.Path]::GetTempPath()) "mcp-in-$PID.jsonl"
    $output = Join-Path ([System.IO.Path]::GetTempPath()) "mcp-out-$PID.jsonl"

    $lines = $Messages | ForEach-Object { $_ | ConvertTo-Json -Depth 12 -Compress }
    [System.IO.File]::WriteAllLines($input, $lines, [System.Text.UTF8Encoding]::new($false))

    $process = Start-Process -FilePath $mcp -PassThru -WindowStyle Hidden `
        -RedirectStandardInput $input -RedirectStandardOutput $output `
        -RedirectStandardError "$output.err"

    if (-not $process.WaitForExit(300000)) {
        $process.Kill()
        throw 'stakeout-mcp が 300 秒で終わらなかった'
    }

    $responses = @()
    foreach ($line in [System.IO.File]::ReadAllLines($output, [System.Text.UTF8Encoding]::new($false))) {
        if ($line.Trim()) { $responses += ($line | ConvertFrom-Json) }
    }

    Remove-Item $input, $output, "$output.err" -ErrorAction SilentlyContinue
    return $responses
}

function ToolPayload {
    param($Response)

    Assert ($null -ne $Response.result) "応答に result が無い: $($Response | ConvertTo-Json -Compress -Depth 5)"
    Assert (-not $Response.result.isError) "道具が失敗した: $($Response.result.content[0].text)"
    return ($Response.result.content[0].text | ConvertFrom-Json)
}

try {
    Step 'ツール定義が 4 KiB に収まる' {
        # 大きさは標準エラーに出す。ErrorActionPreference = Stop のままだと
        # そこへ書いた時点で終了エラーになり、判定にたどり着けない
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $report = (& $mcp --tools 2>&1 | Select-String 'バイト' | Out-String).Trim()
            $exit = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previous
        }

        Assert ($exit -eq 0) "ツール定義が上限を超えている: $report"
        Write-Host "  $report"
    }

    Step 'Harness を起動する' {
        $dir = Join-Path $root 'samples\target\build\x64-BUG_04'
        $exe = Join-Path $dir 'Harness.exe'
        Assert (Test-Path $exe) 'samples/target/build.ps1 -Bug BUG_04 を実行してください'

        $log = Join-Path $dir 'phase7-harness.log'
        $script:harness = Start-Process -FilePath $exe -ArgumentList 'phase7', '--slow' `
            -WorkingDirectory $dir -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"

        Start-Sleep -Seconds 2
        Assert (-not $script:harness.HasExited) 'Harness がすぐに終了した'
        Write-Host "  harness pid=$($script:harness.Id)"
    }

    Step 'MCP の会話が最後まで通る' {
        $line = (Select-String -Path $source -Pattern 'g_ctx\.tick\+\+;' | Select-Object -First 1).LineNumber

        $attachArgs = @{ pid = $script:harness.Id }
        if ($VsPid -gt 0) { $attachArgs['vsPid'] = $VsPid }

        $responses = Converse @(
            @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
                protocolVersion = '2024-11-05'; capabilities = @{}
                clientInfo = @{ name = 'phase7'; version = '1' } } }
            @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
            @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
            @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{
                name = 'stakeout_attach'; arguments = $attachArgs } }
            @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{
                name = 'stakeout_run_until'; arguments = @{
                    location = "nativelib.c:$line"; condition = 'g_ctx.state == 7'
                    exprs = @('g_ctx.state'); timeoutSec = 60 } } }
            @{ jsonrpc = '2.0'; id = 5; method = 'tools/call'; params = @{
                name = 'stakeout_dump'; arguments = @{ expr = 'g_ctx'; depth = 2 } } }
            @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{
                name = 'stakeout_eval'; arguments = @{ expr = 'g_does_not_exist' } } }
            @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{
                name = 'stakeout_detach'; arguments = @{} } }
        )

        # 通知には応答しない。id 付き 7 件だけが返る
        Assert ($responses.Count -eq 7) "応答の数が合わない: $($responses.Count)"

        $initialize = $responses | Where-Object { $_.id -eq 1 }
        Assert ($initialize.result.serverInfo.name -eq 'stakeout') 'initialize が返っていない'

        $tools = $responses | Where-Object { $_.id -eq 2 }
        Assert ($tools.result.tools.Count -ge 8) "道具が少ない: $($tools.result.tools.Count)"

        $attach = ToolPayload ($responses | Where-Object { $_.id -eq 3 })
        Assert ($attach.data.pid -eq $script:harness.Id) 'アタッチ先が違う'

        $runUntil = ToolPayload ($responses | Where-Object { $_.id -eq 4 })
        Assert ($runUntil.data.reachedTarget) '狙った位置に到達していない'
        Assert ($runUntil.data.exprs.'g_ctx.state' -eq '7') "state が 7 でない: $($runUntil.data.exprs.'g_ctx.state')"

        $dump = ToolPayload ($responses | Where-Object { $_.id -eq 5 })
        Assert ($dump.data.Count -ge 3) "dump の要素が少ない: $($dump.data.Count)"
        Assert (($dump.data | ForEach-Object { $_.path }) -contains 'g_ctx.state') '経路が付いていない'

        # 失敗はプロトコルのエラーではなく道具の失敗として返り、hint が残る
        $failure = ($responses | Where-Object { $_.id -eq 6 }).result
        Assert ($failure.isError) '評価できない式が成功扱いになっている'
        Assert ($failure.content[0].text -match 'hint:') "hint が落ちている: $($failure.content[0].text)"

        ToolPayload ($responses | Where-Object { $_.id -eq 7 }) | Out-Null

        Write-Host "  道具 $($tools.result.tools.Count) 個 / dump $($dump.data.Count) 要素"
    }

    Step 'デタッチ後も Target が生きている' {
        Start-Sleep -Seconds 1
        $script:harness.Refresh()
        Assert (-not $script:harness.HasExited) 'デタッチで Target が死んだ'
    }
}
finally {
    & $stakeout daemon stop --json 2>&1 | Out-Null

    if ($script:harness -and -not $script:harness.HasExited) {
        $script:harness.Kill()
    }

    Write-Host ""
    Write-Host "pass=$script:pass fail=$script:fail" -ForegroundColor ($(if ($script:fail -eq 0) { 'Green' } else { 'Red' }))
}

exit ($(if ($script:fail -eq 0) { 0 } else { 1 }))
