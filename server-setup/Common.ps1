# server-setup のスクリプトで共通に使う関数。各スクリプトから . "$PSScriptRoot\Common.ps1" で読み込む。
# Windows PowerShell 5.1 は BOM がないと日本語を正しく読めないので、BOM 付き UTF-8 で保存すること。

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$PublishSettingsPath = Join-Path $PSScriptRoot 'publish-settings.json'

function Write-Step([string] $text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok([string] $text) { Write-Host "   OK: $text" -ForegroundColor Green }

function Write-TextFile([string] $path, [string] $text) {
    [System.IO.File]::WriteAllText($path, $text, $Utf8NoBom)
}

function Read-JsonFile([string] $path) {
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

# 書き込み途中で読まれても壊れないよう、一時ファイルに書いてから置き換える
function Write-JsonFile([string] $path, $data) {
    $tmp = "$path.tmp"
    Write-TextFile $tmp (ConvertTo-Json -InputObject $data -Depth 20)
    Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Read-Value([string] $prompt, [string] $default) {
    $suffix = if ($default) { " [$default]" } else { '' }
    while ($true) {
        $value = Read-Host "$prompt$suffix"
        if (-not $value) { $value = $default }
        if ($value) { return $value.Trim() }
    }
}

function Get-FullPath([string] $path) {
    return [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)).TrimEnd('\', '/')
}

<#
  Publish-Pakset.bat が使う設定（publish-settings.json）。
  {
    "manifest": "C:\\simutrans-dist\\manifest.json",
    "paksets": [ { "pakset_source": "...", "destination": "...", "server_ids": ["friends-a"], "engine_source": "..." } ]
  }
  同じ pakset フォルダを使うサーバーは、1つの項目の server_ids にまとめる。
  engine_source は simutrans 本体のフォルダ（pakset フォルダの1つ上。simutrans.exe がある場所）。なければ本体は公開しない。
#>
function Get-PublishSettings {
    $result = [ordered]@{ manifest = $null; paksets = @() }
    if (-not (Test-Path -LiteralPath $PublishSettingsPath)) {
        return $result
    }
    $saved = Read-JsonFile $PublishSettingsPath
    $result.manifest = $saved.manifest
    if ($saved.PSObject.Properties['paksets']) {
        $result.paksets = @($saved.paksets | ForEach-Object {
            $engine = if ($_.PSObject.Properties['engine_source']) { $_.engine_source } else { $null }
            [ordered]@{ pakset_source = $_.pakset_source; destination = $_.destination; server_ids = @($_.server_ids); engine_source = $engine }
        })
    }
    elseif ($saved.PSObject.Properties['pakset_source']) {
        # 以前の形式（pakset 1つだけ）
        $result.paksets = @([ordered]@{ pakset_source = $saved.pakset_source; destination = $saved.destination; server_ids = @($saved.server_id); engine_source = $null })
    }
    return $result
}

function Save-PublishSettings($settings) {
    Write-JsonFile $PublishSettingsPath ([ordered]@{ manifest = $settings.manifest; paksets = @($settings.paksets) })
}

# pakset を登録する。同じ pakset フォルダが登録済みなら、そこにサーバーの id を足す。
# 本体のフォルダ（pakset フォルダの1つ上）に simutrans.exe があれば、本体も公開の対象にする
function Add-PublishEntry($settings, [string] $source, [string] $destination, [string] $serverId) {
    foreach ($p in $settings.paksets) {
        if ($p.pakset_source -eq $source -and $p.destination -eq $destination) {
            if (-not ($p.server_ids -contains $serverId)) { $p.server_ids = @($p.server_ids) + $serverId }
            if (-not $p.engine_source) { $p.engine_source = Get-EngineSource $source }
            return
        }
    }
    $settings.paksets = @($settings.paksets) + [ordered]@{
        pakset_source = $source; destination = $destination; server_ids = @($serverId); engine_source = (Get-EngineSource $source)
    }
}

# simutrans 本体の実行ファイルを探す（simutrans.exe、なければ simutrans*.exe）
function Find-SimutransExe([string] $dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) { return $null }
    $exes = @(Get-ChildItem -LiteralPath $dir -Filter '*.exe' -File | Where-Object { $_.Name -match '^simutrans.*\.exe$' })
    $main = @($exes | Where-Object { $_.Name -ieq 'simutrans.exe' })
    if ($main.Count -gt 0) { return $main[0].FullName }
    if ($exes.Count -gt 0) { return $exes[0].FullName }
    return $null
}

# pakset フォルダの1つ上に simutrans 本体があれば、そのフォルダを返す
function Get-EngineSource([string] $paksetSource) {
    $parent = Split-Path -Parent $paksetSource
    if (Find-SimutransExe $parent) { return $parent }
    return $null
}
