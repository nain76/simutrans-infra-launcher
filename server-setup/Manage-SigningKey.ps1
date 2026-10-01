<#
.SYNOPSIS
    サーバーリストに署名するための鍵を管理する（確認コードの表示、バックアップ、作り直しなど）。

.DESCRIPTION
    ふだんは使わなくてかまいません。鍵は最初に公開したときに自動で作られ、公開するたびに自動で署名されます。
    次のようなときにManage-SigningKey.batをダブルクリックしてください。
      - 友人に伝える確認コードをもう一度見たい
      - 鍵のバックアップを作りたい
      - manifest.jsonを手で書き換えたので、署名し直したい
      - VPSを作り直したので、バックアップから鍵を戻したい
      - 鍵が盗まれたおそれがあるので作り直したい
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Common.ps1')

$manifestPath = (Get-PublishSettings).manifest
if (-not $manifestPath -or -not (Test-Path -LiteralPath $manifestPath)) {
    $manifestPath = $null
}

function Invoke-Resign {
    if (-not $manifestPath) {
        Write-Warning 'サーバーリスト（manifest.json）が見つかりません。先にSetup-Server.batで構築してください'
        return
    }
    Update-ManifestSignature $manifestPath
}

Write-Step 'サーバーリストの署名の鍵'
Write-SigningExplanation
if (-not (Get-SigningKey)) {
    Initialize-SigningKey $manifestPath
    if ($manifestPath) { Invoke-Resign }
    return
}

while ($true) {
    $key = Get-SigningKey
    Show-SigningCode $key.Code
    Write-Host "   秘密の鍵のファイル: $SigningKeyPath（Windowsユーザー $($key.User)でしか開けません）"
    if ($manifestPath) {
        $published = Get-PublishedSigningCode $manifestPath
        if (-not $published) { Write-Warning 'いまのサーバーリストには署名がありません。3で署名してください' }
        elseif ($published -ne $key.Code) { Write-Warning "いまのサーバーリストは別の鍵（確認コード $published）で署名されています。3で署名し直してください" }
    }
    Write-Host ''
    Write-Host '     1. 確認コードをもう一度表示する'
    Write-Host '     2. バックアップを作る（VPSを作り直したときのため）'
    Write-Host '     3. サーバーリストに署名し直す（manifest.jsonを手で書き換えたあと）'
    Write-Host '     4. バックアップから戻す（VPSを作り直したとき）'
    Write-Host '     5. 鍵を作り直す（鍵が盗まれたおそれがあるときだけ。友人全員に確認し直してもらうことになります）'
    Write-Host '     0. 終わる'
    switch (Read-Value '番号' '0') {
        '1' { continue }
        '2' { Invoke-BackupPrompt }
        '3' { Invoke-Resign }
        '4' {
            Write-Warning '今の鍵はバックアップの鍵で置き換わります'
            Restore-SigningKeyInteractive
            Invoke-Resign
        }
        '5' {
            Write-Warning '鍵を作り直すと確認コードが変わり、友人のランチャーはサーバーリストを読み込めなくなります。'
            Write-Warning '友人には新しい確認コードを伝え、ランチャーの「編集」で入力し直してもらう必要があります。'
            if ((Read-Value '本当に作り直しますか？ 作り直す場合はyesと入力' 'no') -eq 'yes') {
                $old = $key.Code
                Save-SigningKey (New-KeyBytes)
                Write-Ok "鍵を作り直しました（確認コード $old → $((Get-SigningKey).Code)）"
                Invoke-Resign
                Invoke-BackupPrompt
            }
        }
        '0' { return }
        default { Write-Warning '0〜5の番号で答えてください' }
    }
}
