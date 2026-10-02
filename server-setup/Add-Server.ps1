<#
.SYNOPSIS
    サーバーリストにsimutransサーバーを1台追加し、そのサーバーが使うpaksetを公開する。

.DESCRIPTION
    1. サーバー名・simutransサーバーのポート・paksetフォルダのフルパスを聞く（引数で指定してもよい）
    2. サーバーリスト（manifest.json）にサーバーを足す。サーバーリストがなければ作る
    3. paksetを公開フォルダにコピーしてファイル一覧を作る（Publish-Pakset.ps1）
    4. paksetフォルダの1つ上にsimutrans本体があれば、それも公開する（Publish-Engine.ps1）
    5. publish-settings.jsonに登録する。以後はPublish-Pakset.batで全サーバー分を公開し直せる

    同じpaksetフォルダを使うサーバーを追加した場合は、公開済みのpaksetをそのまま共有する。
    中身の違うpaksetを使う場合は、公開用のフォルダ名が重ならないよう自動で名前を変える（例: pak128.japan-2）。
    ソースを見た限り、接続時に照合されるのはpaksetの中身のチェックサムで、フォルダ名は照合されない。

    管理者として実行すること（Add-Server.batをダブルクリックすると管理者として起動する）。
#>
[CmdletBinding()]
param(
    # 公開フォルダ
    [string] $DistDir = 'C:\simutrans-dist',
    # ランチャーに表示するサーバー名
    [string] $ServerName,
    # サーバーリストに書くサーバーのid（省略すると自動で決める）
    [string] $ServerId,
    # 友人が接続に使うドメイン（省略するとサーバーリストの既存のサーバーと同じ）
    [string] $PublicHost,
    # simutransサーバーのポート（省略すると質問する。既定値は空いている番号）
    [int] $GamePort,
    # simutransサーバーが使っているpaksetフォルダのフルパス（省略すると質問する）
    [string] $PaksetSource,
    # 公開するときのフォルダ名（省略すると自動で決める）
    [string] $PaksetFolder
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$manifestPath = Resolve-ManifestPath $DistDir
$data = if (Test-Path -LiteralPath $manifestPath) { Read-JsonFile $manifestPath } else { $null }
$servers = @(if ($data) { $data.servers })
$settings = Get-PublishSettings

Write-Step 'サーバーを追加します'
if ($servers.Count -gt 0) {
    Write-Host '   登録済みのサーバー:'
    foreach ($s in $servers) { Write-Host "     - $($s.name)（$($s.address)、pakset: $($s.pakset.folder)）" }
}

# ---質問---
if (-not $ServerName) { $ServerName = Read-Value 'ランチャーに表示するサーバー名' '' }

if (-not $PublicHost) {
    if ($servers.Count -gt 0) { $PublicHost = ($servers[0].address -split ':')[0] }
    else { $PublicHost = Read-Value '友人が接続に使うドメイン（例: example.ddns.net）' '' }
}

$usedPorts = @($servers | ForEach-Object { [int](($_.address -split ':')[1]) })
if (-not $GamePort) {
    $suggest = 13353
    while ($usedPorts -contains $suggest) { $suggest++ }
    $GamePort = [int](Read-Value "このsimutransサーバーのポート（起動時の-serverに付ける番号）" "$suggest")
}
if ($usedPorts -contains $GamePort) {
    throw "ポート $GamePort は登録済みのサーバーが使っています。simutransサーバーはそれぞれ別のポートで起動してください"
}

while (-not $PaksetSource) {
    $answer = (Read-Host 'このsimutransサーバーが使っているpaksetフォルダのフルパス（例: C:\simutrans-server\pak128.japan）').Trim().Trim('"')
    if ($answer -and (Test-Path -LiteralPath $answer -PathType Container)) { $PaksetSource = $answer }
    elseif ($answer) { Write-Warning "フォルダが見つかりません: $answer" }
}
if (-not (Test-Path -LiteralPath $PaksetSource -PathType Container)) {
    throw "paksetフォルダが見つかりません: $PaksetSource"
}
$PaksetSource = Get-FullPath $PaksetSource

# ---公開用のフォルダ名を決める---
$shared = @($settings.paksets | Where-Object { $_.pakset_source -eq $PaksetSource })
if ($shared.Count -gt 0) {
    # 同じpaksetフォルダを使うサーバーがあれば、公開済みのpaksetを共有する（友人のPCでのフォルダ名も同じにする）
    $destination = $shared[0].destination
    $sharing = @($servers | Where-Object { $shared[0].server_ids -contains $_.id })
    $PaksetFolder = if ($sharing.Count -gt 0) { $sharing[0].pakset.folder } else { Split-Path -Leaf $destination }
    Write-Ok "このpaksetは公開済みのものを共有します（$PaksetFolder）"
}
elseif (-not $PaksetFolder) {
    $base = Split-Path -Leaf $PaksetSource
    $taken = @($settings.paksets | ForEach-Object { Split-Path -Leaf $_.destination }) + @($servers | ForEach-Object { $_.pakset.folder })
    $PaksetFolder = $base
    $n = 2
    while ($taken -contains $PaksetFolder) { $PaksetFolder = "$base-$n"; $n++ }
    if ($PaksetFolder -ne $base) {
        Write-Ok "別のサーバーが $base を使っているので、公開用のフォルダ名を $PaksetFolder にします"
    }
}
if (-not $shared.Count) {
    # 公開フォルダの名前には、推測されにくいランダムな文字を足す（友人のPCでのフォルダ名は $PaksetFolder のまま）
    $destination = Join-Path $DistDir "$PaksetFolder-$(New-RandomName 8)"
}
if ($PaksetFolder -notmatch '^[A-Za-z0-9_.+-]+$') {
    throw "公開用のフォルダ名に使えない文字があります: $PaksetFolder（英数字と_ . + -だけ使えます）"
}

# ---サーバーのid ---
$ids = @($servers | ForEach-Object { $_.id })
if (-not $ServerId) {
    $n = $servers.Count + 1
    $ServerId = if ($n -eq 1) { 'friends-a' } else { "server$n" }
    while ($ids -contains $ServerId) { $n++; $ServerId = "server$n" }
}
if ($ids -contains $ServerId) { throw "id '$ServerId' のサーバーはすでにあります" }

# ---サーバーリストに足す---
$entry = [ordered]@{
    id      = $ServerId
    name    = $ServerName
    address = "${PublicHost}:$GamePort"
    status  = 'online'
    message = ''
    pakset  = [ordered]@{ name = $PaksetFolder; folder = $PaksetFolder }
}
if ($data) {
    $data.servers = @($data.servers) + (New-Object psobject -Property $entry)
}
else {
    $data = [ordered]@{ schema_version = 1; servers = @($entry) }
}
Write-ManifestFile $manifestPath $data
Write-Ok "サーバーリストに「$ServerName」（$PublicHost`:$GamePort）を追加しました"

# ---公開---
$settings.manifest = $manifestPath
Add-PublishEntry $settings $PaksetSource $destination $ServerId
Save-PublishSettings $settings
$entryIds = @($settings.paksets | Where-Object { $_.destination -eq $destination })[0].server_ids

Write-Step "paksetを公開します（$PaksetFolder）"
& (Join-Path $PSScriptRoot 'Publish-Pakset.ps1') -Source $PaksetSource -Destination $destination -Manifest $manifestPath -ServerId $entryIds

# simutrans本体（paksetフォルダの1つ上）も公開する。同じ本体を使うサーバーはまとめて書き換える
$publishEntry = @($settings.paksets | Where-Object { $_.destination -eq $destination })[0]
if (-not $publishEntry.engine_source) {
    $exe = Resolve-EngineExe $PaksetSource @($GamePort)
    $publishEntry.engine_source = if ($exe) { $exe } else { 'none' }
    Save-PublishSettings $settings
}
$engineSource = if ($publishEntry.engine_source -ne 'none') { $publishEntry.engine_source } else { $null }
if ($engineSource) {
    Write-Step "simutrans本体を公開します（$engineSource）"
    $engineIds = @($settings.paksets | Where-Object { $_.engine_source -eq $engineSource } | ForEach-Object { $_.server_ids })
    & (Join-Path $PSScriptRoot 'Publish-Engine.ps1') -Source $engineSource -Destination (Join-Path $DistDir 'engine') -Manifest $manifestPath -ServerId $engineIds
}
else {
    Write-Warning "simutrans本体は公開しません。友人は手元のsimutransを使います"
}

# ---ゲーム用ポートをWindowsファイアウォールで開ける---
if (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue) {
    $ruleName = "simutrans server (TCP $GamePort)"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $GamePort -Action Allow | Out-Null
    }
    Write-Ok "WindowsファイアウォールでTCP $GamePort を開けました"
}

Write-Host ''
Write-Host "追加しました。このsimutransサーバーは-server $GamePort で起動してください" -ForegroundColor Cyan
Write-Host "VPS事業者のパケットフィルターなどがある場合は、そこでもTCP $GamePort を許可してください（8080番を許可したのと同じ場所）" -ForegroundColor Cyan
