<#
.SYNOPSIS
    本体の公開で、許可リストの部品のほかに配るフォルダとファイル（サーバーが追加したもの）を、本体フォルダごとに決める。

.DESCRIPTION
    simutrans本体は、engine-files.json（なければ engine-files.default.json）に書いた部品だけを配る。
    このスクリプトは、サーバーの本体フォルダの直下にあって、まだどの部品にも入っていないフォルダとファイル
    （公開のときに「配らないもの（設定に書いていないため）」と出るもの）から配るものを選び、
    engine-extras.json に本体フォルダごとに書く。公開のとき、その本体フォルダを使うサーバーにだけ
    「サーバーが追加したもの」（id: extra）という部品として届く。

    絶対に配らないもの（スクリプトやバッチファイル、exe、セーブデータ、ログ、save / screenshot / addons など）は
    選べない。選んだフォルダの中にあっても、公開のときに外す。

    書き換えたあとは、Publish-Pakset.bat（サーバー管理ツールの「paksetと本体を公開」）で公開し直すと友人に届く。
    サーバー管理ツールから呼ばれる。

.EXAMPLE
    .\Edit-EngineFiles.ps1 -List
    .\Edit-EngineFiles.ps1 -Source C:\NANSSC3FP2 -Items 'interlockingpanel|station_data.json' -Required $true
#>
[CmdletBinding()]
param(
    # 本体フォルダごとに、選べるものと今の状態を表示する（書き換えない）
    [switch] $List,
    # 書き換える本体フォルダ（本体のexeがあるフォルダ）。本体フォルダが1つだけなら省略できる
    [string] $Source,
    # 追加で配るもの。本体フォルダの直下のフォルダ名かファイル名を「|」で区切る（今の選択をこれで置き換える。空なら追加分なし）
    [string] $Items,
    # 追加分を必須にするか（友人が外せない）。$falseなら推奨（友人が「カスタム」を選べば外せる）
    [Nullable[bool]] $Required
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Move-LegacyEngineExtra

$customPath = Join-Path $PSScriptRoot 'engine-files.json'
$rulesPath = if (Test-Path -LiteralPath $customPath) { $customPath } else { Join-Path $PSScriptRoot 'engine-files.default.json' }
$rules = Read-JsonFile $rulesPath
if (-not $rules.PSObject.Properties['components']) {
    throw "$(Split-Path -Leaf $rulesPath) が以前の形式（componentsがない）です。engine-files.default.json を参考に components の形に書き直してください"
}

function Get-List($obj, [string] $name) {
    if ($obj -and $obj.PSObject.Properties[$name]) { return @($obj.$name) } else { return @() }
}

function Test-Like([string] $name, [string[]] $patterns) {
    foreach ($pattern in $patterns) { if ($name -like $pattern) { return $true } }
    return $false
}

# 許可リストの部品がすでに配っているもの
$ownedFolders = @()
$ownedPatterns = @()
foreach ($c in @($rules.components | Where-Object { $_.id -ne $EngineExtraId })) {
    foreach ($f in (Get-List $c 'folders')) { $ownedFolders += ($f -split '/', 2)[0].ToLowerInvariant() }
    $ownedPatterns += Get-List $c 'files'
}

$settings = Get-PublishSettings
$sources = Get-EngineSourceFolders $settings
if ($sources.Count -eq 0) { throw '本体を公開しているサーバーがありません。先にSetup-Server.batかAdd-Server.batで構築してください' }
$extras = Get-EngineExtras

# 本体フォルダを使っているサーバーの名前（画面でどのサーバーの設定か分かるように）
$serverNames = @{}
if ($settings.manifest -and (Test-Path -LiteralPath $settings.manifest)) {
    foreach ($s in @((Read-JsonFile $settings.manifest).servers)) { $serverNames[$s.id] = $s.name }
}
function Get-ServersFor([string] $folder) {
    $ids = @($settings.paksets | Where-Object {
            $_.engine_source -and $_.engine_source -ne 'none' -and
            (Split-Path -Parent (Get-FullPath $_.engine_source)) -ieq $folder } | ForEach-Object { $_.server_ids })
    return @($ids | Sort-Object -Unique | ForEach-Object { if ($serverNames.ContainsKey($_)) { "$($serverNames[$_])（$_）" } else { $_ } })
}

# 本体フォルダ1つ分の、選べるもの（直下にあって、部品に入っておらず、絶対に配らないものでも pakset でもないもの）
function Get-Candidates([string] $folder, $extra) {
    $selectedFolders = @(Get-List $extra 'folders' | ForEach-Object { $_.ToLowerInvariant() })
    $selectedFiles = @(Get-List $extra 'files' | ForEach-Object { [System.Management.Automation.WildcardPattern]::Unescape($_).ToLowerInvariant() })
    $candidates = [ordered]@{}
    foreach ($item in Get-ChildItem -LiteralPath $folder -Force) {
        if (Test-SkipOsFile $item $folder) { continue }
        $key = $item.Name.ToLowerInvariant()
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
    return $candidates
}

if ($List -or -not $PSBoundParameters.ContainsKey('Items')) {
    # サーバー管理ツールが読む形（1行に1つ、タブ区切り。最初の欄の次は本体フォルダ）
    foreach ($folder in $sources) {
        $extra = Find-EngineExtra $extras $folder
        $required = if ($extra) { [bool]$extra.required } else { $true }
        Write-Output "SOURCE`t$folder`t$((Get-ServersFor $folder) -join '、')"
        Write-Output "REQUIRED`t$folder`t$([int]$required)"
        foreach ($c in (Get-Candidates $folder $extra).Values) { Write-Output "ITEM`t$folder`t$($c.Kind)`t$([int]$c.Selected)`t$($c.Name)" }
    }
    return
}

# 書き換える本体フォルダ
if (-not $Source) {
    if ($sources.Count -gt 1) { throw "本体フォルダが複数あるので、-Sourceでどれかを指定してください: $($sources -join '、')" }
    $Source = $sources[0]
}
$folder = @($sources | Where-Object { $_ -ieq (Get-FullPath $Source).TrimEnd('\', '/') })[0]
if (-not $folder) { throw "公開の設定に登録していない本体フォルダです: $Source" }
$extra = Find-EngineExtra $extras $folder
$candidates = Get-Candidates $folder $extra

# 選んだものを確かめる（選べるものの中からだけ）
$wanted = @(if ($Items) { $Items -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_ } })
$folders = @()
$files = @()
$names = @()
foreach ($name in $wanted) {
    $c = $candidates[$name.ToLowerInvariant()]
    if (-not $c) { throw "選べないものです（本体フォルダの直下にない、すでにほかの部品に入っている、または絶対に配らないもの）: $name" }
    if ($c.Kind -eq 'folder') { $folders += $c.Name; $names += "$($c.Name)\" }
    else { $files += [System.Management.Automation.WildcardPattern]::Escape($c.Name); $names += $c.Name }
}
$isRequired = if ($null -ne $Required) { [bool]$Required } elseif ($extra) { [bool]$extra.required } else { $true }

$others = @($extras | Where-Object { $_.folder.TrimEnd('\', '/') -ine $folder })
$others += [pscustomobject]@{ folder = $folder; required = $isRequired; folders = $folders; files = $files }
Save-EngineExtras $others

$servers = (Get-ServersFor $folder) -join '、'
if ($names.Count -eq 0) {
    Write-Output "$folder（$servers）で追加で配るものはなしにしました"
}
else {
    $kind = if ($isRequired) { '必須' } else { '推奨' }
    Write-Output "$folder（$servers）で追加で配るもの（$kind）: $($names -join '、')"
}
Write-Output 'engine-extras.json に書きました。「paksetと本体を公開」（Publish-Pakset.bat）を実行すると友人に届きます'
