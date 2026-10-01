<#
.SYNOPSIS
    配信サイトをHTTPSにする（Let's Encryptの無料証明書をwin-acmeで取得してIISに設定する）。

.DESCRIPTION
    ファイルの書き換えはサーバーリストの署名（Signing.ps1）で防いでいる。HTTPSにすると通信が暗号化され、
    署名に加えてもう一段守れる（おすすめ）。

    1. WindowsファイアウォールでTCP 80（証明書の確認用）とHTTPSのポート（既定8443）を開ける
    2. win-acme（証明書を取るツール）をGitHubの公式の配布元からHTTPSで入れる。GitHubがSHA256を公開していれば照合する
       （win-acmeは作者が自分で作った証明書で署名しているため、Windowsの電子署名の確認は通らない）
    3. 証明書を取り、IISのサイトにHTTPSのポートを追加する。更新はwin-acmeが自動で行う（タスクスケジューラ）
    4. HTTPSでサーバーリストを取得できるか確かめる

    証明書の取得と更新のたびに、Let's Encryptがhttp://<ドメイン>/（80番）にアクセスして確認する。
    そのため80番は開けたままにしておくこと（VPS事業者のパケットフィルターなども）。
    管理者として実行すること（Enable-Https.batをダブルクリックすると管理者として起動する）。
#>
[CmdletBinding()]
param(
    # 証明書を取るドメイン（省略するとサーバーリストの接続先から取る）
    [string] $HostName,
    # HTTPSのポート
    [int] $HttpsPort = 8443,
    # IISのサイト名
    [string] $SiteName = 'simutrans-dist',
    # 公開フォルダ（サーバーリストの場所を知るため）
    [string] $DistDir = 'C:\simutrans-dist',
    # 証明書の期限切れなどの連絡先（任意）
    [string] $Email,
    # win-acmeを置く場所
    [string] $ToolDir = (Join-Path $env:ProgramData 'simutrans-dist\win-acme')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '管理者として実行してください（Enable-Https.batをダブルクリックすると管理者として起動します）'
}
Import-Module WebAdministration
$site = Get-Website -Name $SiteName
if (-not $site) { throw "IISのサイト $SiteName がありません。先にSetup-Server.batを実行してください" }

if (-not $HostName) {
    $manifestPath = Join-Path $DistDir 'manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        $first = @((Read-JsonFile $manifestPath).servers)[0]
        if ($first) { $HostName = ($first.address -split ':')[0] }
    }
    if (-not $HostName) { $HostName = Read-Value '証明書を取るドメイン（例: example.ddns.net）' '' }
}

# --- 1. ファイアウォール---
Write-Step "WindowsファイアウォールでTCP 80（証明書の確認用）と $HttpsPort を開けます"
foreach ($port in @(80, $HttpsPort)) {
    $ruleName = "simutrans-dist (TCP $port)"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow | Out-Null
    }
}
Write-Ok 'ファイアウォールを設定しました'

# --- 2. win-acme ---
Write-Step 'win-acme（証明書を取るツール）を確認しています'
$wacs = Join-Path $ToolDir 'wacs.exe'
if (-not (Test-Path -LiteralPath $wacs)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/win-acme/win-acme/releases/latest' -Headers @{ 'User-Agent' = 'simutrans-dist-setup' }
    $asset = @($release.assets | Where-Object { $_.name -match '\.x64\.pluggable\.zip$' })[0]
    if (-not $asset) { throw 'win-acmeの配布ファイルが見つかりませんでした' }
    $zip = Join-Path $env:TEMP $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
    $digest = if ($asset.PSObject.Properties['digest']) { $asset.digest } else { $null }
    if ($digest -and $digest -match '^sha256:([0-9a-fA-F]{64})$') {
        $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        if ($actual -ne $Matches[1].ToUpperInvariant()) {
            Remove-Item -LiteralPath $zip -Force
            throw 'win-acmeのダウンロードが壊れています（SHA256が一致しません）。もう一度実行してください'
        }
        Write-Ok "SHA256を確かめました（$($asset.name)）"
    }
    else {
        Write-Host '   GitHubにSHA256が載っていないため、公式の配布元からHTTPSで取得したことを確認の根拠にします'
    }
    New-Item -ItemType Directory -Force -Path $ToolDir | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $ToolDir -Force
    Remove-Item -LiteralPath $zip -Force
}
if (-not (Test-Path -LiteralPath $wacs)) {
    throw "win-acme（wacs.exe）が見つかりません: $ToolDir。フォルダを消してもう一度実行してください"
}
# 参考表示。win-acmeは作者が自分で作った証明書（CN=WACS）で署名しているため、Windowsの確認はValidにならない
$signature = Get-AuthenticodeSignature -LiteralPath $wacs
$signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '署名なし' }
Write-Ok "win-acme: $wacs（署名者: $signer）"

# --- 3. 証明書の取得とIISへの設定---
Write-Step "$HostName の証明書を取り、ポート $HttpsPort でHTTPSを有効にします"
Write-Host "   Let's Encryptがhttp://$HostName/（80番）にアクセスして確認します。VPS事業者のパケットフィルターなどでも80番を許可しておいてください"
$wacsArgs = @(
    '--source', 'manual', '--host', $HostName,
    '--validation', 'selfhosting',
    '--store', 'certificatestore',
    '--installation', 'iis', '--installationsiteid', "$($site.id)", '--sslport', "$HttpsPort",
    '--accepttos', '--closeonfinish'
)
if ($Email) { $wacsArgs += @('--emailaddress', $Email) }
& $wacs @wacsArgs
if ($LASTEXITCODE -ne 0) {
    throw "証明書を取得できませんでした（win-acmeの終了コード $LASTEXITCODE）。80番が外から届くか、$HostName がこのサーバーを指しているか確かめてください"
}

# --- 4. 確認---
Write-Step 'HTTPSで配信できるか確かめています'
$binding = @(Get-WebBinding -Name $SiteName -Protocol https | Where-Object { ($_.bindingInformation -split ':')[1] -eq "$HttpsPort" })
if ($binding.Count -eq 0) {
    Write-Warning "IISのサイト $SiteName にHTTPS（$HttpsPort）の設定が見つかりません"
}
$shareUrl = "https://${HostName}:$HttpsPort/manifest.json"
try {
    $response = Invoke-WebRequest -Uri $shareUrl -UseBasicParsing -TimeoutSec 15
    Write-Ok "$shareUrl を取得できました（$($response.RawContentLength)バイト）"
}
catch {
    Write-Warning "$shareUrl を取得できませんでした: $($_.Exception.Message)"
}

Write-Host ''
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ' HTTPSにしました。残りの作業:' -ForegroundColor Cyan
Write-Host "  1. VPS事業者のパケットフィルターなどでTCP 80とTCP $HttpsPort を許可する（80番は証明書の更新にも使うので開けたままにする）"
Write-Host "  2. 友人に新しいアドレスを伝える: $shareUrl"
Write-Host '     友人はランチャーで「編集」から配信アドレスをこれに変える（確認コードは変わらないので入力し直しは不要）'
Write-Host '  証明書はwin-acmeが自動で更新します（タスクスケジューラに登録済み）'
Write-Host '============================================================' -ForegroundColor Cyan
