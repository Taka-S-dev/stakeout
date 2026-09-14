<#
.SYNOPSIS
  評価でエージェントに見せるソースの写しから、答えの手がかりを消す（ADR 0022）。

.DESCRIPTION
  run.ps1 から dot-source して使う。

  - コメントをすべて消す。サンプルのコメントは原因をそのまま説明している
  - BUG_NN の条件コンパイルを、選んだバグについて解決する。選ばれた枝は無条件のコードになり、
    ビルドに /DBUG_NN が要らなくなる（PDB にコンパイラの引数として残らない）
  - "BUG_NN" という文字列リテラルを消す（nl_active_bug がバグの名前を返す）

  **行番号は保つ。** エージェントが打つ run-until FILE:LINE を、
  写しからビルドした PDB の行と一致させるためである。消した行は空行になる。
#>

<# C のコメントを消す。改行は残す。文字列と文字リテラルの中の // や /* は消さない。 #>
function Remove-CComments {
    param([string]$Text)

    $sb = New-Object System.Text.StringBuilder $Text.Length
    $state = 'code'
    $i = 0

    while ($i -lt $Text.Length) {
        $c = $Text[$i]
        $next = if ($i + 1 -lt $Text.Length) { $Text[$i + 1] } else { [char]0 }

        if ($state -eq 'code') {
            if ($c -eq [char]'/' -and $next -eq [char]'*') { $state = 'block'; $i += 2; continue }
            if ($c -eq [char]'/' -and $next -eq [char]'/') { $state = 'line'; $i += 2; continue }
            if ($c -eq [char]'"') { $state = 'string' }
            elseif ($c -eq [char]"'") { $state = 'char' }
            [void]$sb.Append($c)
        }
        elseif ($state -eq 'block') {
            if ($c -eq [char]'*' -and $next -eq [char]'/') { $state = 'code'; $i += 2; continue }
            if ($c -eq [char]"`r" -or $c -eq [char]"`n") { [void]$sb.Append($c) }
        }
        elseif ($state -eq 'line') {
            if ($c -eq [char]"`r" -or $c -eq [char]"`n") { $state = 'code'; [void]$sb.Append($c) }
        }
        else {
            # 文字列か文字リテラル。エスケープの次の 1 文字は閉じ引用符として扱わない
            [void]$sb.Append($c)
            if ($c -eq [char]'\' -and $i + 1 -lt $Text.Length) {
                [void]$sb.Append($next)
                $i += 2
                continue
            }
            $quote = if ($state -eq 'string') { [char]'"' } else { [char]"'" }
            if ($c -eq $quote) { $state = 'code' }
        }

        $i++
    }

    return $sb.ToString()
}

<#
  BUG_NN の条件コンパイルだけを解決する。インクルードガードなど、それ以外の条件は残す。
  扱うのは #ifdef / #ifndef BUG_NN、#if defined(BUG_NN)、#elif defined(BUG_NN)、#else、#endif。
  **それ以外の形で BUG_NN が現れたら解決せずに残す。** 残れば漏れの検査で落ちる。
#>
function Resolve-BugConditionals {
    param([string[]]$Lines, [string]$Bug)

    $stack = New-Object System.Collections.Generic.List[object]
    $result = New-Object System.Collections.Generic.List[string]

    foreach ($line in $Lines) {
        $t = $line.Trim()
        $active = -not ($stack | Where-Object { $_.IsBug -and -not $_.Active })
        $top = if ($stack.Count -gt 0) { $stack[$stack.Count - 1] } else { $null }

        if ($t -match '^#\s*if(n?)def\s+(BUG_\d+)$' -or $t -match '^#\s*if\s+(!?)\s*defined\s*\(\s*(BUG_\d+)\s*\)$') {
            $hit = $Matches[2] -eq $Bug
            if ($Matches[1]) { $hit = -not $hit }
            $stack.Add([pscustomobject]@{ IsBug = $true; Taken = $hit; Active = $hit })
            $result.Add('')
            continue
        }

        if ($t -match '^#\s*if') {
            $stack.Add([pscustomobject]@{ IsBug = $false; Taken = $false; Active = $true })
            $result.Add($(if ($active) { $line } else { '' }))
            continue
        }

        if ($t -match '^#\s*(elif|else|endif)\b') {
            if ($null -eq $top) { throw "対応する #if がありません: $t" }

            if (-not $top.IsBug) {
                if ($Matches[1] -eq 'endif') { $stack.RemoveAt($stack.Count - 1) }
                $result.Add($(if ($active) { $line } else { '' }))
                continue
            }

            switch ($Matches[1]) {
                'elif' {
                    if ($t -notmatch '^#\s*elif\s+defined\s*\(\s*(BUG_\d+)\s*\)$') {
                        throw "BUG_NN の #elif は defined(BUG_NN) の形だけを扱います: $t"
                    }
                    $hit = (-not $top.Taken) -and ($Matches[1] -eq $Bug)
                    $top.Active = $hit
                    if ($hit) { $top.Taken = $true }
                }
                'else' {
                    $top.Active = -not $top.Taken
                    $top.Taken = $true
                }
                'endif' {
                    $stack.RemoveAt($stack.Count - 1)
                }
            }

            $result.Add('')
            continue
        }

        $result.Add($(if ($active) { $line } else { '' }))
    }

    if ($stack.Count -gt 0) { throw '#if が閉じていません' }

    return , $result.ToArray()
}

<# 評価用の写しを作る。行数は変えない。 #>
function ConvertTo-EvalSource {
    param([string]$Text, [string]$Bug)

    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = (Remove-CComments $Text) -split "\r?\n"
    $resolved = Resolve-BugConditionals -Lines $lines -Bug $Bug

    $joined = ($resolved | ForEach-Object { $_.TrimEnd() }) -join $newline
    return [regex]::Replace($joined, '"BUG_\d+"', '"n/a"')
}

<#
  写しに答えの手がかりが残っていないかを調べる。残っている行を返す。
  サンプルのコメントは日本語なので、非 ASCII が残っていればコメントの消し漏れである。
#>
function Find-EvalLeaks {
    param([string]$Text)

    $lines = $Text -split "\r?\n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch 'BUG_\d+' -or $lines[$i] -match '[^\x00-\x7F]') {
            "line $($i + 1): $($lines[$i].Trim())"
        }
    }
}
