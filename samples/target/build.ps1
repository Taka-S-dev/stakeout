<#
.SYNOPSIS
  samples/target をビルドする。MSVC (cl.exe) が要る。
.DESCRIPTION
  vswhere で MSVC を探し、vcvars で環境を取り込んでから NativeLib.dll と Harness.exe を
  デバッグ構成（/Zi /Od、PDB あり）でビルドする。出力は samples/target/build/<arch>/。
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86')]
    [string]$Arch = 'x64',

    # 仕込みバグを 1 つ有効にする（design.md §18.1）。既定はバグ無し
    [ValidateSet('none', 'BUG_01', 'BUG_03', 'BUG_04', 'BUG_05')]
    [string]$Bug = 'none',

    # NativeLib/ と Harness/ のあるディレクトリ。既定はこのスクリプトの隣。
    # 評価スイートは、答えの手がかりを消した写しを渡す（ADR 0022）
    [string]$SourceRoot,

    # 出力先。既定は build/<arch>[-<bug>]/
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = if ($SourceRoot) { (Resolve-Path $SourceRoot).Path } else { $root }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe が見つからない: $vswhere" }

$vsPath = & $vswhere -latest -prerelease -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'MSVC ツールセットを持つ Visual Studio が見つからない' }

$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars$(if ($Arch -eq 'x64') { '64' } else { '32' }).bat"
if (-not (Test-Path $vcvars)) { throw "vcvars が見つからない: $vcvars" }

# vcvars はバッチなので、cmd 経由で環境変数を取り込む
& cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
}

$suffix = if ($Bug -eq 'none') { $Arch } else { "$Arch-$Bug" }
$outDir = if ($OutDir) { [System.IO.Path]::GetFullPath($OutDir) } else { Join-Path $root "build\$suffix" }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Push-Location $outDir
try {
    # 共通フラグ: UTF-8 ソース・デバッグ情報あり・最適化なし・警告をエラー扱い
    $cflags = @('/nologo', '/utf-8', '/W4', '/WX', '/Zi', '/Od', '/MTd', '/D_CRT_SECURE_NO_WARNINGS')
    if ($Bug -ne 'none') {
        # 仕込みバグは意図的に危険なコードを通すので、その分だけ警告を緩める
        $cflags += "/D$Bug"
        $cflags = $cflags | Where-Object { $_ -ne '/WX' }
        Write-Host "bug: $Bug"
    }

    Write-Host "building NativeLib.dll ($Arch)"
    & cl @cflags /LD "$sourceRoot\NativeLib\nativelib.c" /Fe:NativeLib.dll /Fd:NativeLib.pdb
    if ($LASTEXITCODE -ne 0) { throw "NativeLib のビルドに失敗した ($LASTEXITCODE)" }

    Write-Host "building Harness.exe ($Arch)"
    & cl @cflags "$sourceRoot\Harness\harness.c" /Fe:Harness.exe /Fd:Harness.pdb /link NativeLib.lib
    if ($LASTEXITCODE -ne 0) { throw "Harness のビルドに失敗した ($LASTEXITCODE)" }

    Write-Host "ok -> $outDir"
    Get-ChildItem $outDir -Include *.exe, *.dll, *.pdb -Recurse | Select-Object Name, Length
}
finally {
    Pop-Location
}
