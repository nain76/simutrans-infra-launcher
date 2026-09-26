<#
.SYNOPSIS
    ランチャー向けの配信サーバー（IIS）を初回構築する。何度実行しても同じ状態になる。

.DESCRIPTION
    1. IIS（Windows 標準の Web サーバー機能）を入れる
    2. 公開フォルダ（既定: C:\simutrans-dist）を作り、指定ポート（既定: 8080）で公開するサイトを作る
    3. Windows ファイアウォールでそのポートを開ける
    4. サーバーリスト（manifest.json）がなければ作る
    5. pakset フォルダ（-PaksetSource。なければ質問する）を Publish-Pakset.ps1 で公開し、
       その設定を publish-settings.json に残す（以後は Publish-Pakset.bat だけで公開し直せる）
    6. 実際にサーバーリストを取得できるか確かめ、友人に伝えるアドレスを表示する

    足りない情報は実行中に質問するので、引数なしで実行してもよい。
    管理者として実行すること（Setup-Server.bat をダブルクリックすると管理者として起動する）。

.EXAMPLE
    .\Install-DistServer.ps1 -PublicHost example.ddns.net -ServerName "友達内輪鯖A" `
        -PaksetSource C:\simutrans-server\pak128.japan
#>
[CmdletBinding()]
param(
    # 配信に使うポート
    [int] $Port = 8080,
    # 公開フォルダ
    [string] $DistDir = 'C:\simutrans-dist',
    # IIS のサイト名
    [string] $SiteName = 'simutrans-dist',
    # 友人が接続に使うドメイン（例: example.ddns.net）
    [string] $PublicHost,
    # サーバーリストに書くサーバー名
    [string] $ServerName,
    # サーバーリストに書くサーバーの id
    [string] $ServerId = 'friends-a',
    # simutrans サーバーのポート
    [int] $GamePort = 13353,
    # simutrans サーバーが使っている pakset フォルダのフルパス（省略すると質問する）
    [string] $PaksetSource
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-Step([string] $text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok([string] $text) { Write-Host "   OK: $text" -ForegroundColor Green }

function Read-Value([string] $prompt, [string] $default) {
    $suffix = if ($default) { " [$default]" } else { '' }
    while ($true) {
        $value = Read-Host "$prompt$suffix"
        if (-not $value) { $value = $default }
        if ($value) { return $value.Trim() }
    }
}

# --- 0. 確認 ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '管理者として実行してください（Setup-Server.bat をダブルクリックすると管理者として起動します）'
}
if ($Port -lt 1 -or $Port -gt 65535) { throw "ポート番号が正しくありません: $Port" }

# --- 1. IIS ---
Write-Step 'IIS（Web サーバー機能）を確認しています'
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
    # Windows Server
    $result = Install-WindowsFeature Web-Server, Web-Static-Content -IncludeManagementTools
    if ($result.RestartNeeded -eq 'Yes') {
        Write-Warning 'IIS を使うには Windows の再起動が必要です。再起動してから、もう一度このスクリプトを実行してください'
        return
    }
}
else {
    # Windows 10 / 11
    $result = Enable-WindowsOptionalFeature -Online -All -NoRestart -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-StaticContent, IIS-ManagementConsole
    if ($result.RestartNeeded) {
        Write-Warning 'IIS を使うには Windows の再起動が必要です。再起動してから、もう一度このスクリプトを実行してください'
        return
    }
}
Import-Module WebAdministration
Write-Ok 'IIS が使えます'

# --- 2. 公開フォルダとサイト ---
Write-Step "公開フォルダ $DistDir をポート $Port で公開します"
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

$conflict = @(Get-WebBinding | Where-Object {
    $_.protocol -eq 'http' -and ($_.bindingInformation -split ':')[1] -eq "$Port" -and
    $_.ItemXPath -notmatch "@name='$([regex]::Escape($SiteName))'"
})
if ($conflict.Count -gt 0) {
    throw "ポート $Port は IIS の別のサイトが使っています。-Port で別の番号を指定してください"
}

if (Get-Website -Name $SiteName) {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $DistDir
    Get-WebBinding -Name $SiteName | Remove-WebBinding
    New-WebBinding -Name $SiteName -Protocol http -Port $Port -IPAddress '*'
}
else {
    New-Website -Name $SiteName -PhysicalPath $DistDir -Port $Port | Out-Null
}
# フォルダの中身の一覧表示は明示的にオフにする
Set-WebConfigurationProperty -PSPath "IIS:\Sites\$SiteName" -Filter /system.webServer/directoryBrowse -Name enabled -Value $false
Start-Website -Name $SiteName
Write-Ok "サイト $SiteName をポート $Port で公開しました"

# --- 3. ファイアウォール ---
Write-Step "Windows ファイアウォールでポート $Port を開けます"
$ruleName = "simutrans-dist (TCP $Port)"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
}
Write-Ok "ルール「$ruleName」があります"

# --- 4. pakset フォルダ ---
Write-Step '公開する pakset フォルダを確認しています'
while (-not $PaksetSource) {
    $answer = Read-Host 'simutrans サーバーが使っている pakset フォルダのフルパス（例: C:\simutrans-server\pak128.japan。空欄なら今回は公開しない）'
    if (-not $answer) { break }
    $answer = $answer.Trim().Trim('"')
    if (Test-Path -LiteralPath $answer -PathType Container) { $PaksetSource = $answer }
    else { Write-Warning "フォルダが見つかりません: $answer" }
}
$paksetFolder = $null
if ($PaksetSource) {
    if (-not (Test-Path -LiteralPath $PaksetSource -PathType Container)) {
        throw "pakset フォルダが見つかりません: $PaksetSource"
    }
    $PaksetSource = [System.IO.Path]::GetFullPath($PaksetSource).TrimEnd('\', '/')
    $paksetFolder = Split-Path -Leaf $PaksetSource
    Write-Ok "pakset フォルダ: $PaksetSource"
}
else {
    Write-Warning 'pakset は公開しません。あとで Publish-Pakset.bat（または -PaksetSource を付けてこのスクリプト）で公開してください'
}

# --- 5. サーバーリスト ---
Write-Step 'サーバーリスト（manifest.json）を確認しています'
$manifestPath = Join-Path $DistDir 'manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $data = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $server = @($data.servers | Where-Object { $_.id -eq $ServerId })
    if ($server.Count -ne 1) {
        $ids = (@($data.servers) | ForEach-Object { $_.id }) -join ', '
        throw "既存のサーバーリストに id が '$ServerId' のサーバーがありません（あるのは: $ids）。-ServerId で指定してください"
    }
    # 公開先のフォルダ名はサーバーリストの pakset.folder に合わせる
    $paksetFolder = $server[0].pakset.folder
    if (-not $PublicHost) { $PublicHost = ($server[0].address -split ':')[0] }
    Write-Ok "既存のサーバーリストをそのまま使います: $manifestPath"
}
else {
    if (-not $PublicHost) { $PublicHost = Read-Value '友人が接続に使うドメイン（例: example.ddns.net）' '' }
    if (-not $ServerName) { $ServerName = Read-Value 'ランチャーに表示するサーバー名' '友達内輪鯖' }
    if (-not $paksetFolder) { $paksetFolder = Read-Value 'pakset のフォルダ名' 'pak128.japan' }
    $data = [ordered]@{
        schema_version = 1
        servers        = @(
            [ordered]@{
                id      = $ServerId
                name    = $ServerName
                address = "${PublicHost}:$GamePort"
                status  = 'online'
                message = ''
                pakset  = [ordered]@{ name = $paksetFolder; folder = $paksetFolder }
            }
        )
    }
    [System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $data -Depth 10), $Utf8NoBom)
    Write-Ok "サーバーリストを作りました: $manifestPath"
}

# --- 6. pakset の公開 ---
if ($PaksetSource) {
    Write-Step 'pakset を公開します'
    $publishSettings = [ordered]@{
        pakset_source = $PaksetSource
        destination   = (Join-Path $DistDir $paksetFolder)
        manifest      = $manifestPath
        server_id     = $ServerId
    }
    # Publish-Pakset.bat（引数なしの Publish-Pakset.ps1）が使う設定を残す
    [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'publish-settings.json'), (ConvertTo-Json -InputObject $publishSettings), $Utf8NoBom)
    & (Join-Path $PSScriptRoot 'Publish-Pakset.ps1') -Source $PaksetSource -Destination $publishSettings.destination `
        -Manifest $manifestPath -ServerId $ServerId
}

# --- 7. 確認 ---
Write-Step '配信できるか確かめています'
$localUrl = "http://localhost:$Port/manifest.json"
try {
    $response = Invoke-WebRequest -Uri $localUrl -UseBasicParsing -TimeoutSec 10
    Write-Ok "$localUrl を取得できました（$($response.RawContentLength) バイト）"
    if ($paksetFolder -and (Test-Path -LiteralPath (Join-Path $DistDir "$paksetFolder\index.json"))) {
        $index = Invoke-WebRequest -Uri "http://localhost:$Port/$paksetFolder/index.json" -UseBasicParsing -TimeoutSec 10
        Write-Ok "pakset のファイル一覧も取得できました（$($index.RawContentLength) バイト）"
    }
}
catch {
    Write-Warning "$localUrl を取得できませんでした: $($_.Exception.Message)"
}

# --- まとめ ---
$shareUrl = if ($PublicHost) { "http://${PublicHost}:$Port/manifest.json" } else { "http://<ドメイン>:$Port/manifest.json" }
Write-Host ''
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ' 構築が終わりました。残りの作業:' -ForegroundColor Cyan
Write-Host "  1. 外から TCP $Port 番に届くようにする（simutrans の $GamePort 番を開けたのと同じ場所で）"
Write-Host "     - VPS の場合: 事業者の管理画面のパケットフィルター / セキュリティグループで TCP $Port を許可する"
Write-Host "       （その仕組みがない事業者なら不要。Windows のファイアウォールはこのツールで開けました）"
Write-Host "     - 自宅の場合: ルーターのポート転送で TCP $Port をこのサーバーへ転送する"
Write-Host "  2. 自分の PC のブラウザで $shareUrl が開けるか確かめる"
Write-Host "  3. 友人にこのアドレスを伝える: $shareUrl"
Write-Host '     友人はランチャーの「追加」→「サーバー管理者から共有されたリストを追加」に入れる'
if ($PaksetSource) {
    Write-Host '  アドオンを足したあとは: pak をコピー → simutrans サーバーを再起動 → Publish-Pakset.bat をダブルクリック'
}
else {
    Write-Host '  4. pakset を公開する: もう一度 Setup-Server.bat を実行し、pakset フォルダのフルパスを答える'
}
Write-Host '============================================================' -ForegroundColor Cyan
