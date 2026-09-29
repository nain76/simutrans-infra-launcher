<#
.SYNOPSIS
    サーバーリストに simutrans サーバーを1台追加し、そのサーバーが使う pakset を公開する。

.DESCRIPTION
    1. サーバー名・simutrans サーバーのポート・pakset フォルダのフルパスを聞く（引数で指定してもよい）
    2. サーバーリスト（manifest.json）にサーバーを足す。サーバーリストがなければ作る
    3. pakset を公開フォルダにコピーしてファイル一覧を作る（Publish-Pakset.ps1）
    4. pakset フォルダの1つ上に simutrans 本体があれば、それも公開する（Publish-Engine.ps1）
    5. publish-settings.json に登録する。以後は Publish-Pakset.bat で全サーバー分を公開し直せる

    同じ pakset フォルダを使うサーバーを追加した場合は、公開済みの pakset をそのまま共有する。
    中身の違う pakset を使う場合は、公開用のフォルダ名が重ならないよう自動で名前を変える（例: pak128.japan-2）。
    ソースを見た限り、接続時に照合されるのは pakset の中身のチェックサムで、フォルダ名は照合されない。

    管理者として実行すること（Add-Server.bat をダブルクリックすると管理者として起動する）。
#>
[CmdletBinding()]
param(
    # 公開フォルダ
    [string] $DistDir = 'C:\simutrans-dist',
    # ランチャーに表示するサーバー名
    [string] $ServerName,
    # サーバーリストに書くサーバーの id（省略すると自動で決める）
    [string] $ServerId,
    # 友人が接続に使うドメイン（省略するとサーバーリストの既存のサーバーと同じ）
    [string] $PublicHost,
    # simutrans サーバーのポート（省略すると質問する。既定値は空いている番号）
    [int] $GamePort,
    # simutrans サーバーが使っている pakset フォルダのフルパス（省略すると質問する）
    [string] $PaksetSource,
    # 公開するときのフォルダ名（省略すると自動で決める）
    [string] $PaksetFolder
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$manifestPath = Join-Path $DistDir 'manifest.json'
$data = if (Test-Path -LiteralPath $manifestPath) { Read-JsonFile $manifestPath } else { $null }
$servers = @(if ($data) { $data.servers })
$settings = Get-PublishSettings

Write-Step 'サーバーを追加します'
if ($servers.Count -gt 0) {
    Write-Host '   登録済みのサーバー:'
    foreach ($s in $servers) { Write-Host "     - $($s.name)（$($s.address)、pakset: $($s.pakset.folder)）" }
}

# --- 質問 ---
if (-not $ServerName) { $ServerName = Read-Value 'ランチャーに表示するサーバー名' '' }

if (-not $PublicHost) {
    if ($servers.Count -gt 0) { $PublicHost = ($servers[0].address -split ':')[0] }
    else { $PublicHost = Read-Value '友人が接続に使うドメイン（例: example.ddns.net）' '' }
}

$usedPorts = @($servers | ForEach-Object { [int](($_.address -split ':')[1]) })
if (-not $GamePort) {
    $suggest = 13353
    while ($usedPorts -contains $suggest) { $suggest++ }
    $GamePort = [int](Read-Value "この simutrans サーバーのポート（起動時の -server に付ける番号）" "$suggest")
}
if ($usedPorts -contains $GamePort) {
    throw "ポート $GamePort は登録済みのサーバーが使っています。simutrans サーバーはそれぞれ別のポートで起動してください"
}

while (-not $PaksetSource) {
    $answer = (Read-Host 'この simutrans サーバーが使っている pakset フォルダのフルパス（例: C:\simutrans-server\pak128.japan）').Trim().Trim('"')
    if ($answer -and (Test-Path -LiteralPath $answer -PathType Container)) { $PaksetSource = $answer }
    elseif ($answer) { Write-Warning "フォルダが見つかりません: $answer" }
}
if (-not (Test-Path -LiteralPath $PaksetSource -PathType Container)) {
    throw "pakset フォルダが見つかりません: $PaksetSource"
}
$PaksetSource = Get-FullPath $PaksetSource

# --- 公開用のフォルダ名を決める ---
$shared = @($settings.paksets | Where-Object { $_.pakset_source -eq $PaksetSource })
if ($shared.Count -gt 0) {
    # 同じ pakset フォルダを使うサーバーがあれば、公開済みの pakset を共有する
    $PaksetFolder = Split-Path -Leaf $shared[0].destination
    Write-Ok "この pakset は公開済みのものを共有します（$PaksetFolder）"
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
if ($PaksetFolder -notmatch '^[A-Za-z0-9_.+-]+$') {
    throw "公開用のフォルダ名に使えない文字があります: $PaksetFolder（英数字と _ . + - だけ使えます）"
}

# --- サーバーの id ---
$ids = @($servers | ForEach-Object { $_.id })
if (-not $ServerId) {
    $n = $servers.Count + 1
    $ServerId = if ($n -eq 1) { 'friends-a' } else { "server$n" }
    while ($ids -contains $ServerId) { $n++; $ServerId = "server$n" }
}
if ($ids -contains $ServerId) { throw "id '$ServerId' のサーバーはすでにあります" }

# --- サーバーリストに足す ---
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
Write-JsonFile $manifestPath $data
Write-Ok "サーバーリストに「$ServerName」（$PublicHost`:$GamePort）を追加しました"

# --- 公開 ---
$settings.manifest = $manifestPath
$destination = Join-Path $DistDir $PaksetFolder
Add-PublishEntry $settings $PaksetSource $destination $ServerId
Save-PublishSettings $settings
$entryIds = @($settings.paksets | Where-Object { $_.destination -eq $destination })[0].server_ids

Write-Step "pakset を公開します（$PaksetFolder）"
& (Join-Path $PSScriptRoot 'Publish-Pakset.ps1') -Source $PaksetSource -Destination $destination -Manifest $manifestPath -ServerId $entryIds

# simutrans 本体（pakset フォルダの1つ上）も公開する。同じ本体を使うサーバーはまとめて書き換える
$publishEntry = @($settings.paksets | Where-Object { $_.destination -eq $destination })[0]
if (-not $publishEntry.engine_source) {
    $exe = Resolve-EngineExe $PaksetSource
    $publishEntry.engine_source = if ($exe) { $exe } else { 'none' }
    Save-PublishSettings $settings
}
$engineSource = if ($publishEntry.engine_source -ne 'none') { $publishEntry.engine_source } else { $null }
if ($engineSource) {
    Write-Step "simutrans 本体を公開します（$engineSource）"
    $engineIds = @($settings.paksets | Where-Object { $_.engine_source -eq $engineSource } | ForEach-Object { $_.server_ids })
    & (Join-Path $PSScriptRoot 'Publish-Engine.ps1') -Source $engineSource -Destination (Join-Path $DistDir 'engine') -Manifest $manifestPath -ServerId $engineIds
}
else {
    Write-Warning "simutrans 本体は公開しません。友人は手元の simutrans を使います"
}

# --- ゲーム用ポートを Windows ファイアウォールで開ける ---
if (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue) {
    $ruleName = "simutrans server (TCP $GamePort)"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $GamePort -Action Allow | Out-Null
    }
    Write-Ok "Windows ファイアウォールで TCP $GamePort を開けました"
}

Write-Host ''
Write-Host "追加しました。この simutrans サーバーは -server $GamePort で起動してください" -ForegroundColor Cyan
Write-Host "VPS 事業者のパケットフィルターなどがある場合は、そこでも TCP $GamePort を許可してください（8080 番を許可したのと同じ場所）" -ForegroundColor Cyan
