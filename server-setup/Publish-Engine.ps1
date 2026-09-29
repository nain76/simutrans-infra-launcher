<#
.SYNOPSIS
    simutrans サーバーの本体（Windows 版）を、部品ごとのファイル一覧つきで公開し、サーバーリストに書く。

.DESCRIPTION
    友人のランチャーは、サーバーと同じ本体を自動で入れて起動する（本体が違うとチェックサムがずれることがあるため）。
    1. -Source（simutrans 本体の exe）があるフォルダから、配るものだけを集める（許可リスト方式）
       - 推奨設定は engine-files.default.json。engine-files.json があればそちらを使う（カスタム）
       - 本体の exe は必ず入れる
       - 絶対に配らないもの（設定でも変わらない）: スクリプトやバッチファイル、本体以外の exe（nettool / makeobj / ほかの版）、
         セーブデータ（.sve）、settings.xml、ログ、save / screenshot / addons フォルダ、pakset のフォルダ
       部品（音楽、テーマなど）ごとに「必須・推奨・任意」を決め、友人はランチャーで「推奨」か「カスタム」を選べる
    2. 中身から識別名（revision）を決める
    3. -Destination\<revision>\ にファイルとファイル一覧（index.json）を置き、サーバーリストの該当サーバーの engine を書き換える
       （ファイル一覧方式なので、友人は本体を更新したときも変わったファイルだけを落とす）
    4. どのサーバーも使わなくなった古い版のフォルダ（と以前の zip）を消す

    ランチャーは、ユーザーが確認コードを登録した鍵で署名されたサーバーリストからしか本体を入れない
    （すり替えられないようにするため）。署名はサーバーリストを書き換えるたびに自動で付ける（Signing.ps1）。

    引数なしの Publish-Pakset.ps1（Publish-Pakset.bat）から自動で呼ばれる。
#>
[CmdletBinding()]
param(
    # simutrans 本体の exe のフルパス（フォルダを渡すと中の exe を探す）
    [Parameter(Mandatory = $true)] [string] $Source,
    # 本体を置くフォルダ（サーバーリストと同じフォルダの下。この中に版ごとのフォルダを作る）
    [Parameter(Mandatory = $true)] [string] $Destination,
    # 書き換えるサーバーリスト（manifest.json）
    [Parameter(Mandatory = $true)] [string] $Manifest,
    # この本体を使うサーバーの id
    [Parameter(Mandatory = $true)] [string[]] $ServerId
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

# 絶対に配らないもの（engine-files.json に書いても変わらない）
$NeverFolders = @('save', 'screenshot', 'addons')
$NeverFiles = @('*.bat', '*.cmd', '*.ps1', '*.psm1', '*.vbs', '*.vbe', '*.js', '*.jse', '*.wsf', '*.hta', '*.lnk', '*.url', '*.reg',
    '*.exe', '*.com', '*.scr', '*.msi', '*.sve', '*.log', 'settings.xml', '*pwdhash*', 'index.json', 'web.config')

# 配るもの（推奨設定。engine-files.json があればそちらを使う）
$rulesPath = Join-Path $PSScriptRoot 'engine-files.json'
$rulesName = 'engine-files.json（カスタム）'
if (-not (Test-Path -LiteralPath $rulesPath)) {
    $rulesPath = Join-Path $PSScriptRoot 'engine-files.default.json'
    $rulesName = '推奨設定（engine-files.default.json）'
}
if (-not (Test-Path -LiteralPath $rulesPath)) { throw "配るものの設定がありません: $rulesPath" }
$rules = Read-JsonFile $rulesPath
$components = @()
if ($rules.PSObject.Properties['components']) {
    foreach ($c in $rules.components) {
        $folders = if ($c.PSObject.Properties['folders']) { @($c.folders | ForEach-Object { $_.ToLowerInvariant() }) } else { @() }
        $patterns = if ($c.PSObject.Properties['files']) { @($c.files) } else { @() }
        $components += [pscustomobject]@{
            Id          = $c.id
            Name        = $c.name
            Required    = [bool]($c.PSObject.Properties['required'] -and $c.required)
            Recommended = [bool]($c.PSObject.Properties['recommended'] -and $c.recommended)
            Folders     = $folders
            Files       = $patterns
        }
    }
}
else {
    # 以前の形式（folders と files だけ）は、必須の部品1つとして扱う
    $components += [pscustomobject]@{ Id = 'core'; Name = '本体'; Required = $true; Recommended = $false
        Folders = @($rules.folders | ForEach-Object { $_.ToLowerInvariant() }); Files = @($rules.files) }
}
$coreComponent = @($components | Where-Object { $_.Required })[0]
if (-not $coreComponent) { throw "$rulesPath に必須（required）の部品がありません" }
foreach ($c in $components) {
    if ($c.Id -notmatch '^[A-Za-z0-9_.+-]+$') { throw "部品の id に使えない文字があります: $($c.Id)" }
}

function Test-Like([string] $name, [string[]] $patterns) {
    foreach ($pattern in $patterns) { if ($name -like $pattern) { return $true } }
    return $false
}

$dst = Get-FullPath $Destination
$manifestPath = Get-FullPath $Manifest
$given = Get-FullPath $Source
if (Test-Path -LiteralPath $given -PathType Leaf) {
    $exe = $given
    $src = Split-Path -Parent $exe
}
else {
    $src = $given
    $exe = Find-SimutransExe $src
    if (-not $exe) {
        throw "simutrans 本体の exe を決められません: $src（exe のフルパスを指定してください）"
    }
}
$manifestDir = Split-Path -Parent $manifestPath
if (-not $dst.StartsWith($manifestDir + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "-Destination はサーバーリストと同じフォルダの下にしてください: $dst"
}

# --- 1. 配るファイルを部品ごとに集める ---
$exeName = Split-Path -Leaf $exe
$files = @([pscustomobject]@{ Rel = $exeName; File = (Get-Item -LiteralPath $exe); Component = $coreComponent.Id })
$skippedFolders = @()
$neverItems = @()
foreach ($item in Get-ChildItem -LiteralPath $src -Force) {
    if ($item.PSIsContainer) {
        $isPakset = $null -ne (Get-ChildItem -LiteralPath $item.FullName -Filter '*.pak' -File -Force | Select-Object -First 1)
        if ($isPakset -or ($NeverFolders -contains $item.Name.ToLowerInvariant())) { $neverItems += "$($item.Name)\"; continue }
        $owner = @($components | Where-Object { $_.Folders -contains $item.Name.ToLowerInvariant() })[0]
        if (-not $owner) { $skippedFolders += $item.Name; continue }
        foreach ($f in Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Force) {
            # フォルダの中でも、スクリプトや exe などは配らない
            if (Test-Like $f.Name $NeverFiles) { $neverItems += $f.FullName.Substring($src.Length + 1); continue }
            $files += [pscustomobject]@{ Rel = $f.FullName.Substring($src.Length + 1).Replace('\', '/'); File = $f; Component = $owner.Id }
        }
    }
    elseif ($item.Name -ne $exeName) {
        $isDll = $item.Extension -ieq '.dll'
        if (-not $isDll -and (Test-Like $item.Name $NeverFiles)) { $neverItems += $item.Name; continue }
        $owner = @($components | Where-Object { Test-Like $item.Name $_.Files })[0]
        if ($owner) { $files += [pscustomobject]@{ Rel = $item.Name; File = $item; Component = $owner.Id } }
    }
}
$files = @($files | Sort-Object -Property Rel -CaseSensitive)

Write-Host "   配るものの設定: $rulesName"
foreach ($c in $components) {
    $mine = @($files | Where-Object { $_.Component -eq $c.Id })
    $kind = if ($c.Required) { '必須' } elseif ($c.Recommended) { '推奨' } else { '任意' }
    $size = ($mine | Measure-Object -Property { $_.File.Length } -Sum).Sum
    Write-Host ("   部品「{0}」（{1}）: {2} ファイル、{3:N0} バイト" -f $c.Name, $kind, $mine.Count, [long]$size)
}
if ($skippedFolders.Count -gt 0) { Write-Host ("   配らなかったフォルダ: {0}（配るには engine-files.json の部品の folders に足す）" -f ($skippedFolders -join ', ')) }
if ($neverItems.Count -gt 0) {
    $shown = @($neverItems | Select-Object -First 12)
    $more = if ($neverItems.Count -gt $shown.Count) { " ほか $($neverItems.Count - $shown.Count) 件" } else { '' }
    Write-Host ("   絶対に配らないもの: {0}{1}" -f ($shown -join ', '), $more)
}

# --- 2. 識別名（中身と部品の分け方が同じなら同じ名前になる） ---
$hashes = @{}
$lines = foreach ($f in $files) {
    $h = (Get-FileHash -LiteralPath $f.File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashes[$f.Rel] = $h
    "{0}|{1}|{2}|{3}" -f $f.Rel, $f.File.Length, $h, $f.Component
}
$lines += @($components | ForEach-Object { "component|{0}|{1}|{2}|{3}" -f $_.Id, $_.Name, $_.Required, $_.Recommended })
$sha = [System.Security.Cryptography.SHA256]::Create()
$fingerprint = ([System.BitConverter]::ToString($sha.ComputeHash($Utf8NoBom.GetBytes(($lines -join "`n")))) -replace '-', '').ToLowerInvariant()
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if (-not $version) { $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion }
if (-not $version) {
    # 版の情報がない exe（OTRP の sim-WinGDI64-OTRPv57_0_1.exe など）は名前を使う
    $version = [System.IO.Path]::GetFileNameWithoutExtension($exe)
}
$version = ($version -replace '[^A-Za-z0-9_.-]+', '-').Trim('-', '.')
$revision = if ($version) { "$version-$($fingerprint.Substring(0, 8))" } else { "r-$($fingerprint.Substring(0, 8))" }

# --- 3. ファイルとファイル一覧を置く ---
$revDir = Join-Path $dst $revision
$created = -not (Test-Path -LiteralPath (Join-Path $revDir 'index.json'))
if ($created) {
    $staging = "$revDir.partial"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    foreach ($f in $files) {
        $target = Join-Path $staging ($f.Rel.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $f.File.FullName -Destination $target -Force
    }
    $index = [ordered]@{
        schema_version = 1
        components     = @($components | ForEach-Object { [ordered]@{ id = $_.Id; name = $_.Name; required = $_.Required; recommended = $_.Recommended } })
        files          = @($files | ForEach-Object { [ordered]@{ path = $_.Rel; size = [long]$_.File.Length; sha256 = $hashes[$_.Rel]; component = $_.Component } })
    }
    Write-TextFile (Join-Path $staging 'index.json') (ConvertTo-Json -InputObject $index -Depth 5)
    # IIS で配信できるように、使っている拡張子を登録する
    $extensions = @($files | ForEach-Object { $_.File.Extension.ToLowerInvariant() } | Where-Object { $_ -and $_ -ne '.json' } | Sort-Object -Unique)
    $maps = ($extensions | ForEach-Object {
        "      <remove fileExtension=`"$_`" />`r`n      <mimeMap fileExtension=`"$_`" mimeType=`"application/octet-stream`" />"
    }) -join "`r`n"
    Write-TextFile (Join-Path $staging 'web.config') @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by Publish-Engine.ps1. simutrans 本体のファイルを IIS から配信できるようにする。 -->
<configuration>
  <system.webServer>
    <staticContent>
$maps
    </staticContent>
  </system.webServer>
</configuration>
"@
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Move-Item -LiteralPath $staging -Destination $revDir
}
$indexPath = Join-Path $revDir 'index.json'
$indexSha = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash.ToLowerInvariant()
$indexUrl = $indexPath.Substring($manifestDir.Length + 1).Replace('\', '/')

$data = Read-JsonFile $manifestPath
foreach ($id in $ServerId) {
    $server = @($data.servers | Where-Object { $_.id -eq $id })
    if ($server.Count -ne 1) { throw "サーバーリストに id が '$id' のサーバーが見つかりません" }
    $engine = [ordered]@{
        revision = $revision
        builds   = [ordered]@{ 'windows-x64' = [ordered]@{ index_url = $indexUrl; index_sha256 = $indexSha; exe = $exeName } }
    }
    $server[0] | Add-Member -NotePropertyName engine -NotePropertyValue $engine -Force
}
$data | Add-Member -NotePropertyName updated_at -NotePropertyValue (Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz') -Force
Write-ManifestFile $manifestPath $data

# --- 4. どのサーバーも使わなくなった古い版を消す ---
$data = Read-JsonFile $manifestPath
$inUse = @($data.servers | Where-Object { $_.PSObject.Properties['engine'] -and $_.engine } | ForEach-Object {
    $b = $_.engine.builds.PSObject.Properties['windows-x64']
    if ($b) {
        if ($b.Value.PSObject.Properties['index_url']) { ($b.Value.index_url -split '/')[-2] }
        elseif ($b.Value.PSObject.Properties['url']) { ($b.Value.url -split '/')[-1] }
    }
})
foreach ($old in Get-ChildItem -LiteralPath $dst -Force) {
    if ($inUse -contains $old.Name) { continue }
    if ($old.PSIsContainer -and (Test-Path -LiteralPath (Join-Path $old.FullName 'index.json'))) { Remove-Item -LiteralPath $old.FullName -Recurse -Force }
    elseif (-not $old.PSIsContainer -and $old.Name -like 'simutrans-*-windows-x64.zip') { Remove-Item -LiteralPath $old.FullName -Force }
}

$total = ($files | Measure-Object -Property { $_.File.Length } -Sum).Sum
$state = if ($created) { '公開しました' } else { '前回と同じなので置き直していません' }
Write-Host ("本体を公開しました: {0}（{1} ファイル、{2:N0} バイト、{3}）" -f $revision, $files.Count, [long]$total, $state)
Write-Host "サーバーリストの engine を更新しました（$($ServerId -join ', ')、revision $revision）"
