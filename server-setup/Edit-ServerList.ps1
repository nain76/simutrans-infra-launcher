<#
.SYNOPSIS
    サーバーリストのサーバーの表示名・お知らせ・メンテナンス中の表示を書き換えて、署名し直す。

.DESCRIPTION
    サーバー管理ツール（Simutrans_ServerManager.exe）から呼ばれる。質問はせず、引数だけで動く。
    書き換えたあとは、ほかの公開スクリプトと同じく自動で署名する。
    署名の鍵がないときは書き換えずに止まる（鍵は Manage-SigningKey.bat で作る）。

.EXAMPLE
    .\Edit-ServerList.ps1 -ServerId friends-a -Message "今日20時から再開します" -Maintenance $false
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ServerId,
    # 新しい表示名（省略すると変えない）
    [string] $Name,
    # 新しいお知らせ（省略すると変えない。空にしたいときは -ClearMessage）
    [string] $Message,
    [switch] $ClearMessage,
    # メンテナンス中にするか（省略すると変えない）
    [Nullable[bool]] $Maintenance
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$settings = Get-PublishSettings
if (-not $settings.manifest -or -not (Test-Path -LiteralPath $settings.manifest)) {
    throw 'サーバーリストが見つかりません。先に Setup-Server.bat で構築してください'
}
if (-not (Get-SigningKey)) {
    throw '署名の鍵がありません。Manage-SigningKey.bat で鍵を作ってから、もう一度保存してください'
}
$manifestPath = Get-FullPath $settings.manifest
$data = Read-JsonFile $manifestPath
$server = @($data.servers | Where-Object { $_.id -eq $ServerId })
if ($server.Count -ne 1) { throw "サーバーリストに id が '$ServerId' のサーバーが見つかりません" }
$server = $server[0]

if ($PSBoundParameters.ContainsKey('Name')) {
    if (-not $Name.Trim()) { throw '表示名を空にはできません' }
    $server | Add-Member -NotePropertyName name -NotePropertyValue $Name.Trim() -Force
}
if ($ClearMessage) {
    if ($server.PSObject.Properties['message']) { $server.PSObject.Properties.Remove('message') }
}
elseif ($PSBoundParameters.ContainsKey('Message')) {
    $server | Add-Member -NotePropertyName message -NotePropertyValue $Message.Trim() -Force
}
if ($null -ne $Maintenance) {
    # 稼働中かどうかはランチャーが実際につないで確かめるので、メンテナンス中でなければ online にしておく
    $server | Add-Member -NotePropertyName status -NotePropertyValue $(if ($Maintenance) { 'maintenance' } else { 'online' }) -Force
}
$data | Add-Member -NotePropertyName updated_at -NotePropertyValue (Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz') -Force
Write-ManifestFile $manifestPath $data
Write-Host "サーバー「$($server.name)」の情報を保存しました"
