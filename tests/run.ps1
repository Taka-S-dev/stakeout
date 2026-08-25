<#
.SYNOPSIS
  すべてのテストプロジェクトを実行する。
.DESCRIPTION
  dotnet test は .NET SDK 10 環境で xunit v3 のテストを検出できない（ADR 0010）。
  xunit v3 のテストプロジェクトは実行可能ファイルなので、直接走らせて結果を集約する。
#>
[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$projects = Get-ChildItem -Path (Join-Path $root 'tests') -Filter '*.Tests.csproj' -Recurse

if (-not $projects) { throw 'テストプロジェクトが見つからない' }

$failed = @()
foreach ($project in $projects) {
    Write-Host ""
    Write-Host "=== $($project.BaseName) ===" -ForegroundColor Cyan

    $args = @('run', '--project', $project.FullName)
    if ($NoBuild) { $args += '--no-build' }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) { $failed += $project.BaseName }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "すべてのテストプロジェクトが成功した" -ForegroundColor Green
exit 0
