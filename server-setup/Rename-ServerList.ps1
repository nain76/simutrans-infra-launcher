<#
.SYNOPSIS
    サーバーリストのファイル名と、paksetの公開フォルダの名前を、推測されにくいランダムな名前に変える。

.DESCRIPTION
    manifest.json のような決まった名前だと、ドメインとポートを知っている人に中身を見つけられやすくなる。
    このスクリプトは次のことをする。
      1. サーバーリストを list-ランダム.json という名前に変える（署名も作り直す）
      2. paksetの公開フォルダ（例: pak.NSOTRP32）を pak.NSOTRP32-ランダム に変える
         （友人のPCでのフォルダ名は変わらないので、友人が pakset を落とし直すことはない）
      3. publish-settings.json を新しい名前に合わせる
    名前を変えると、友人に伝えたアドレスは使えなくなる。確認コードは変わらないので、
    友人はランチャーの「編集」で配信アドレスを新しいものに変えるだけでよい。
    ファイルを中で移すだけなので、すぐ終わる。
#>
[CmdletBinding()]
param(
    # 確認せずに実行する（サーバー管理ツールから呼ぶとき）
    [switch] $Yes
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$settings = Get-PublishSettings
if (-not $settings.manifest -or -not (Test-Path -LiteralPath $settings.manifest)) {
    throw 'サーバーリストが見つかりません。先に Setup-Server.bat で構築してください'
}
if (-not (Get-SigningKey)) {
    throw '署名の鍵がありません。先に Manage-SigningKey.bat で鍵を作ってください'
}
$oldManifest = Get-FullPath $settings.manifest
$distDir = Split-Path -Parent $oldManifest
$newManifest = Join-Path $distDir "list-$(New-RandomName 10).json"

# 名前を変える pakset の公開フォルダ（すでにランダムな文字が付いているものは変えない）
$moves = @()
foreach ($p in $settings.paksets) {
    $leaf = Split-Path -Leaf $p.destination
    if ($leaf -match '-[a-z2-9]{8}$') { continue }
    $moves += [pscustomobject]@{ Entry = $p; Old = (Get-FullPath $p.destination); New = (Join-Path (Split-Path -Parent (Get-FullPath $p.destination)) "$leaf-$(New-RandomName 8)") }
}

Write-Step 'サーバーリストと公開フォルダの名前を、推測されにくい名前に変えます'
Write-Host "   サーバーリスト: $(Split-Path -Leaf $oldManifest) → $(Split-Path -Leaf $newManifest)"
foreach ($m in $moves) { Write-Host "   paksetの公開フォルダ: $(Split-Path -Leaf $m.Old) → $(Split-Path -Leaf $m.New)" }
Write-Host '   名前を変えると、友人に伝えたアドレスは使えなくなります。'
Write-Host '   確認コードは変わらないので、友人はランチャーの「編集」で配信アドレスを変えるだけで済みます。'
if (-not $Yes -and (Read-Value '名前を変えますか？（y/N）' 'N') -notmatch '^[Yy]') {
    Write-Host '変えませんでした'
    return
}

$data = Read-JsonFile $oldManifest
foreach ($m in $moves) {
    if (Test-Path -LiteralPath $m.Old) { Move-Item -LiteralPath $m.Old -Destination $m.New }
    $oldLeaf = Split-Path -Leaf $m.Old
    $newLeaf = Split-Path -Leaf $m.New
    # サーバーリストの index_url（サーバーリストから見た相対パス）を新しいフォルダ名にする
    foreach ($s in @($data.servers)) {
        if ($m.Entry.server_ids -contains $s.id -and $s.pakset.PSObject.Properties['index_url'] -and $s.pakset.index_url -like "$oldLeaf/*") {
            $s.pakset.index_url = $newLeaf + $s.pakset.index_url.Substring($oldLeaf.Length)
        }
    }
    $m.Entry.destination = $m.New
}

Write-ManifestFile $newManifest $data
$oldSig = Get-SignaturePath $oldManifest
Remove-Item -LiteralPath $oldManifest -Force
if (Test-Path -LiteralPath $oldSig) { Remove-Item -LiteralPath $oldSig -Force }

$settings.manifest = $newManifest
if ($settings.share_url) {
    $settings.share_url = Get-ShareUrl ($settings.share_url.Substring(0, $settings.share_url.LastIndexOf('/'))) $newManifest
}
Save-PublishSettings $settings

Write-Host ''
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ' 名前を変えました' -ForegroundColor Cyan
if ($settings.share_url) {
    Write-Host "  新しいアドレス: $($settings.share_url)" -ForegroundColor Yellow
}
else {
    Write-Host "  新しいアドレスの最後は $(Split-Path -Leaf $newManifest) です（https://<ドメイン>:8443/$(Split-Path -Leaf $newManifest) など）" -ForegroundColor Yellow
}
Write-Host '  友人にこのアドレスを伝え、ランチャーの「編集」で配信アドレスを変えてもらってください。'
Write-Host '  確認コードは変わらないので、入力し直す必要はありません。'
Write-Host '============================================================' -ForegroundColor Cyan
