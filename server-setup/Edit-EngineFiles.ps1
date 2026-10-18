<#
.SYNOPSIS
    本体の公開で、許可リストの部品のほかに配るフォルダとファイル（サーバーが追加したもの）を決める。

.DESCRIPTION
    simutrans本体は、engine-files.json（なければ engine-files.default.json）に書いた部品だけを配る。
    このスクリプトは、サーバーの本体フォルダの直下にあって、まだどの部品にも入っていないフォルダとファイル
    （公開のときに「配らないもの（設定に書いていないため）」と出るもの）から、配るものを選んで
    engine-files.json の「サーバーが追加したもの」（id: extra）という部品に書く。
    engine-files.json がなければ、推奨設定をコピーしてから書き足す。

    絶対に配らないもの（スクリプトやバッチファイル、exe、セーブデータ、ログ、save / screenshot / addons など）は
    選べない。選んだフォルダの中にあっても、公開のときに外す。

    書き換えたあとは、Publish-Pakset.bat（サーバー管理ツールの「paksetと本体を公開」）で公開し直すと友人に届く。
    サーバー管理ツールから呼ばれる。

.EXAMPLE
    .\Edit-EngineFiles.ps1 -List
    .\Edit-EngineFiles.ps1 -Items 'generated-scripts|station_data.json' -Required $true
#>
[CmdletBinding()]
param(
    # 選べるものと今の状態を表示する（書き換えない）
    [switch] $List,
    # 追加で配るもの。本体フォルダの直下のフォルダ名かファイル名を「|」で区切る（今の選択をこれで置き換える。空なら追加分なし）
    [string] $Items,
    # 追加分を必須にするか（友人が外せない）。$falseなら推奨（友人が「カスタム」を選べば外せる）
    [Nullable[bool]] $Required
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExtraId = 'extra'
$ExtraName = 'サーバーが追加したもの'

$customPath = Join-Path $PSScriptRoot 'engine-files.json'
$defaultPath = Join-Path $PSScriptRoot 'engine-files.default.json'
$rulesPath = if (Test-Path -LiteralPath $customPath) { $customPath } else { $defaultPath }
$rules = Read-JsonFile $rulesPath
if (-not $rules.PSObject.Properties['components']) {
    throw "$(Split-Path -Leaf $rulesPath) が以前の形式（componentsがない）です。engine-files.default.json を参考に components の形に書き直してください"
}
$others = @($rules.components | Where-Object { $_.id -ne $ExtraId })
$extra = @($rules.components | Where-Object { $_.id -eq $ExtraId })[0]

function Get-List($obj, [string] $name) {
    if ($obj -and $obj.PSObject.Properties[$name]) { return @($obj.$name) } else { return @() }
}

# ほかの部品がすでに配っているもの
$ownedFolders = @()
$ownedPatterns = @()
foreach ($c in $others) {
    foreach ($f in (Get-List $c 'folders')) { $ownedFolders += ($f -split '/', 2)[0].ToLowerInvariant() }
    $ownedPatterns += Get-List $c 'files'
}
$selectedFolders = @(Get-List $extra 'folders' | ForEach-Object { $_.ToLowerInvariant() })
$selectedFiles = @(Get-List $extra 'files' | ForEach-Object { [System.Management.Automation.WildcardPattern]::Unescape($_).ToLowerInvariant() })
$isRequired = if ($extra) { [bool]($extra.PSObject.Properties['required'] -and $extra.required) } else { $true }

function Test-Like([string] $name, [string[]] $patterns) {
    foreach ($pattern in $patterns) { if ($name -like $pattern) { return $true } }
    return $false
}

# サーバーの本体フォルダ（公開の設定に登録した本体のexeがあるフォルダ）
$settings = Get-PublishSettings
$sources = @($settings.paksets | ForEach-Object { $_.engine_source } |
    Where-Object { $_ -and $_ -ne 'none' -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
    ForEach-Object { Split-Path -Parent (Get-FullPath $_) } | Sort-Object -Unique)
if ($sources.Count -eq 0) { throw '本体を公開しているサーバーがありません。先にSetup-Server.batかAdd-Server.batで構築してください' }

# 選べるもの: 直下にあって、ほかの部品に入っておらず、絶対に配らないものでも pakset でもないもの
$candidates = [ordered]@{}
foreach ($src in $sources) {
    foreach ($item in Get-ChildItem -LiteralPath $src -Force) {
        if (Test-SkipOsFile $item $src) { continue }
        $key = $item.Name.ToLowerInvariant()
        if ($candidates.Contains($key)) { continue }
        if ($item.PSIsContainer) {
            if ($EngineNeverFolders -contains $key -or $ownedFolders -contains $key) { continue }
            $isPakset = $null -ne (Get-ChildItem -LiteralPath $item.FullName -Filter '*.pak' -File -Force | Select-Object -First 1)
            if ($isPakset -and -not ($selectedFolders -contains $key)) { continue }
            $candidates[$key] = [pscustomobject]@{ Kind = 'folder'; Name = $item.Name; Selected = ($selectedFolders -contains $key) }
        }
        else {
            if ($item.Extension -ieq '.dll' -or (Test-Like $item.Name $EngineNeverFiles) -or (Test-Like $item.Name $ownedPatterns)) { continue }
            $candidates[$key] = [pscustomobject]@{ Kind = 'file'; Name = $item.Name; Selected = ($selectedFiles -contains $key) }
        }
    }
}

if ($List -or $PSBoundParameters.Count -eq 0) {
    # サーバー管理ツールが読む形（1行に1つ、タブ区切り）
    foreach ($src in $sources) { Write-Output "SOURCE`t$src" }
    Write-Output "REQUIRED`t$([int]$isRequired)"
    foreach ($c in $candidates.Values) { Write-Output "ITEM`t$($c.Kind)`t$([int]$c.Selected)`t$($c.Name)" }
    return
}

# 選んだものを確かめる（選べるものの中からだけ）
$wanted = @(if ($Items) { $Items -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_ } })
$folders = @()
$files = @()
foreach ($name in $wanted) {
    $c = $candidates[$name.ToLowerInvariant()]
    if (-not $c) { throw "選べないものです（本体フォルダの直下にない、すでにほかの部品に入っている、または絶対に配らないもの）: $name" }
    if ($c.Kind -eq 'folder') { $folders += $c.Name } else { $files += [System.Management.Automation.WildcardPattern]::Escape($c.Name) }
}
if ($null -ne $Required) { $isRequired = [bool]$Required }

# engine-files.json に書く（なければ推奨設定をコピーしてから）
$components = @($others | ForEach-Object { $_ })
if ($folders.Count + $files.Count -gt 0) {
    $component = [ordered]@{ id = $ExtraId; name = $ExtraName }
    if ($isRequired) { $component.required = $true } else { $component.recommended = $true }
    if ($folders.Count -gt 0) { $component.folders = $folders }
    if ($files.Count -gt 0) { $component.files = $files }
    $components += [pscustomobject]$component
}
$output = [ordered]@{}
if ($rules.PSObject.Properties['_comment']) { $output._comment = $rules._comment }
$output.components = $components
Write-JsonFile $customPath $output

if ($folders.Count + $files.Count -eq 0) {
    Write-Output '追加で配るものはなしにしました'
}
else {
    $kind = if ($isRequired) { '必須' } else { '推奨' }
    Write-Output "追加で配るもの（$kind）: $(@($folders | ForEach-Object { "$_\" }) + @($wanted | Where-Object { $candidates[$_.ToLowerInvariant()].Kind -eq 'file' }) -join '、')"
}
Write-Output 'engine-files.json に書きました。「paksetと本体を公開」（Publish-Pakset.bat）を実行すると友人に届きます'
