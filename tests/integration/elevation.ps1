<#
.SYNOPSIS
  管理者デーモンに、通常権限のクライアントから繋げるかを確かめる（ADR 0024）。

.DESCRIPTION
  **管理者のシェルから実行する。** UAC を通した pwsh で:
    pwsh tests/integration/elevation.ps1

  行うこと:
  1. 設定 off（既定）で管理者デーモンを起動し、通常権限の CLI が拒否されること（DAEMON_UNREACHABLE + 昇格の hint）
  2. 設定 on で管理者デーモンを起動し、通常権限の CLI と管理者の CLI の両方が繋がること
  3. デーモンのログに、接続ごとの client pid / exe / elevated が残ること

  通常権限の CLI は、このスクリプト内で runas ではなく「制限付きトークン」で起動する
  （Explorer 経由の起動と同じ整合性レベルになる）。

  設定 on はユーザー設定 %APPDATA%\stakeout\stakeout.json に書く。**既存のユーザー設定は退避し、終わったら戻す。**
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '管理者のシェルから実行してください（このスクリプトが管理者デーモンを起動します）'
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$daemon = Join-Path $root 'src\Stakeout.Daemon\bin\Debug\net8.0-windows\stakeoutd.exe'
$stakeout = Join-Path $root 'src\Stakeout.Cli\bin\Debug\net8.0\stakeout.exe'
if (-not (Test-Path $daemon)) { throw "stakeoutd.exe がありません。dotnet build を実行してください: $daemon" }

$env:STAKEOUT_PIPE = "stakeout-elevation-$PID"
$userDir = Join-Path $env:APPDATA 'stakeout'
$userConfig = Join-Path $userDir 'stakeout.json'
$backup = "$userConfig.elevation-test.bak"

$script:pass = 0
$script:fail = 0

function Step([string]$Name, [scriptblock]$Body) {
    Write-Host "=== $Name ==="
    try { & $Body; $script:pass++; Write-Host '  OK' }
    catch { $script:fail++; Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red }
}

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

# 通常権限で stakeout を走らせる。runas /trustlevel:0x20000 は「基本ユーザー」の制限付きトークンで起動する
function UnelevatedStakeout([string[]]$Arguments) {
    $out = Join-Path $env:TEMP "stakeout-elevation-$PID-out.txt"
    if (Test-Path $out) { Remove-Item $out }
    $cmd = "`"$stakeout`" $($Arguments -join ' ') > `"$out`" 2>&1"
    $p = Start-Process runas.exe -ArgumentList '/trustlevel:0x20000', "`"cmd /c $cmd`"" -PassThru -WindowStyle Hidden -Wait
    for ($i = 0; $i -lt 20 -and -not (Test-Path $out); $i++) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Milliseconds 500
    if (Test-Path $out) { Get-Content $out -Raw } else { '' }
}

function StartDaemon {
    $p = Start-Process $daemon -ArgumentList '--detach' -WorkingDirectory $root -PassThru
    Start-Sleep -Seconds 3
    Assert (-not $p.HasExited) 'デーモンが起動直後に終了した'
    return $p
}

function StopDaemon($p) {
    & $stakeout daemon stop --json 2>&1 | Out-Null
    Start-Sleep -Seconds 1
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}

function LatestLog { Get-ChildItem "$env:LOCALAPPDATA\stakeout\logs" -Filter '*.jsonl' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }

$hadUserConfig = Test-Path $userConfig
if ($hadUserConfig) { Copy-Item $userConfig $backup -Force }

try {
    # ---- 1. 設定 off
    if ($hadUserConfig) { Remove-Item $userConfig }
    $d = StartDaemon
    try {
        Step '設定 off: 通常権限の CLI は拒否され、昇格の hint が返る' {
            $text = UnelevatedStakeout @('daemon', 'status', '--json')
            Assert ($text -match 'DAEMON_UNREACHABLE') "DAEMON_UNREACHABLE でない: $text"
            Assert ($text -match 'allowUnelevatedClients') "hint に設定名が無い: $text"
        }
        Step '設定 off: 管理者の CLI は繋がる' {
            $text = & $stakeout daemon status --json 2>&1 | Out-String
            Assert ($text -match '"isElevated": true') "繋がらない: $text"
        }
    }
    finally { StopDaemon $d }

    # ---- 2. 設定 on
    New-Item -ItemType Directory -Force $userDir | Out-Null
    [IO.File]::WriteAllText($userConfig, '{ "pipe": { "allowUnelevatedClients": true } }', (New-Object System.Text.UTF8Encoding $false))
    $d = StartDaemon
    try {
        Step '設定 on: 起動ログに ACL の種別が出る' {
            $line = Get-Content (LatestLog).FullName -First 1
            Assert ($line -match 'allowUnelevatedClients') "起動ログに無い: $line"
        }
        Step '設定 on: 通常権限の CLI が繋がる' {
            $text = UnelevatedStakeout @('daemon', 'status', '--json')
            Assert ($text -match '"ok": true') "繋がらない: $text"
            Assert ($text -match '"isElevated": true') "管理者のデーモンでない: $text"
        }
        Step '設定 on: 管理者の CLI も繋がる（所有者検査の自前フォールバック）' {
            $text = & $stakeout daemon status --json 2>&1 | Out-String
            Assert ($text -match '"ok": true') "繋がらない: $text"
        }
        Step '設定 on: 接続ごとに相手の pid と昇格状態がログに残る' {
            $lines = @(Get-Content (LatestLog).FullName | Select-String 'client pid=')
            Assert ($lines.Count -ge 2) "client の行が $($lines.Count) 件しか無い"
            Assert (($lines -join ' ') -match 'elevated=False') '通常権限の接続が記録されていない'
            Assert (($lines -join ' ') -match 'elevated=True') '管理者の接続が記録されていない'
            $lines | ForEach-Object { Write-Host "  $($_.Line.Substring(0, [Math]::Min(160, $_.Line.Length)))" }
        }
    }
    finally { StopDaemon $d }

    # ---- 3. プロジェクト設定に書くと止まる
    Step 'プロジェクト設定に書くと、デーモンが理由を言って起動しない' {
        $dir = Join-Path $env:TEMP "stakeout-elevation-$PID-proj"
        New-Item -ItemType Directory -Force $dir | Out-Null
        [IO.File]::WriteAllText((Join-Path $dir '.stakeout.json'), '{ "pipe": { "allowUnelevatedClients": true } }')
        $err = Join-Path $dir 'stderr.txt'
        $p = Start-Process $daemon -WorkingDirectory $dir -PassThru -WindowStyle Hidden -RedirectStandardError $err -Wait
        Assert ($p.ExitCode -ne 0) '起動してしまった'
        $text = Get-Content $err -Raw
        Assert ($text -match 'allowUnelevatedClients') "理由に設定名が無い: $text"
        Remove-Item -Recurse -Force $dir
    }
}
finally {
    if ($hadUserConfig) { Move-Item $backup $userConfig -Force }
    elseif (Test-Path $userConfig) { Remove-Item $userConfig }
}

Write-Host ""
Write-Host "pass=$script:pass fail=$script:fail"
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
