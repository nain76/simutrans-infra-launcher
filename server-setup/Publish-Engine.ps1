<#
.SYNOPSIS
    simutransサーバーの本体（Windows版）を、部品ごとのファイル一覧つきで公開し、サーバーリストに書く。

.DESCRIPTION
    友人のランチャーは、サーバーと同じ本体を自動で入れて起動する（本体が違うとチェックサムがずれることがあるため）。
    1. -Source（simutrans本体のexe）があるフォルダから、配るものだけを集める（許可リスト方式）
       - 推奨設定はengine-files.default.json。engine-files.jsonがあればそちらを使う（カスタム）
       - 本体のexeは必ず入れる
       - 絶対に配らないもの（設定でも変わらない）: スクリプトやバッチファイル、本体以外のexe（nettool / makeobj /ほかの版）、
         セーブデータ（.sve）、settings.xml、ログ、save / screenshot / addonsフォルダ、paksetのフォルダ
       部品（音楽、テーマなど）ごとに「必須・推奨・任意」を決め、友人はランチャーで「推奨」か「カスタム」を選べる
    2. 中身から識別名（revision）を決める
    3. -Destination\<revision>\にファイルとファイル一覧（index.json）を置き、サーバーリストの該当サーバーのengineを書き換える
       （ファイル一覧方式なので、友人は本体を更新したときも変わったファイルだけを落とす）
    4. どのサーバーも使わなくなった古い版のフォルダ（と以前のzip）を消す

    ランチャーは、ユーザーが確認コードを登録した鍵で署名されたサーバーリストからしか本体を入れない
    （すり替えられないようにするため）。署名はサーバーリストを書き換えるたびに自動で付ける（Signing.ps1）。

    引数なしのPublish-Pakset.ps1（Publish-Pakset.bat）から自動で呼ばれる。
#>
[CmdletBinding()]
param(
    # simutrans本体のexeのフルパス（フォルダを渡すと中のexeを探す）
    [Parameter(Mandatory = $true)] [string] $Source,
    # 本体を置くフォルダ（サーバーリストと同じフォルダの下。この中に版ごとのフォルダを作る）
    [Parameter(Mandatory = $true)] [string] $Destination,
    # 書き換えるサーバーリスト（manifest.json）
    [Parameter(Mandatory = $true)] [string] $Manifest,
    # この本体を使うサーバーのid
    [Parameter(Mandatory = $true)] [string[]] $ServerId
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

# 絶対に配らないもの（engine-files.jsonに書いても変わらない）
$NeverFolders = @('save', 'screenshot', 'addons')
$NeverFiles = @('*.bat', '*.cmd', '*.ps1', '*.psm1', '*.vbs', '*.vbe', '*.js', '*.jse', '*.wsf', '*.hta', '*.lnk', '*.url', '*.reg',
    '*.exe', '*.com', '*.scr', '*.msi', '*.sve', '*.sv_', '*.log', 'settings.xml', '*pwdhash*', 'index.json', 'web.config')

# 配るもの（推奨設定。engine-files.jsonがあればそちらを使う）
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
        # "themes/*.tab" のように書いたフォルダは、直下にある、名前が合うファイルだけを配る（中のフォルダは配らない）
        $entries = if ($c.PSObject.Properties['folders']) { @($c.folders | ForEach-Object { $_.ToLowerInvariant() }) } else { @() }
        $folders = @($entries | Where-Object { $_ -notmatch '/' })
        $flat = @{}
        foreach ($e in @($entries | Where-Object { $_ -match '/' })) {
            $name, $pattern = $e -split '/', 2
            if (-not $flat.ContainsKey($name)) { $flat[$name] = @() }
            $flat[$name] += $pattern
        }
        $patterns = if ($c.PSObject.Properties['files']) { @($c.files) } else { @() }
        $components += [pscustomobject]@{
            Id          = $c.id
            Name        = $c.name
            Required    = [bool]($c.PSObject.Properties['required'] -and $c.required)
            Recommended = [bool]($c.PSObject.Properties['recommended'] -and $c.recommended)
            Folders     = $folders
            FlatFolders = $flat
            Files       = $patterns
        }
    }
}
else {
    # 以前の形式（foldersとfilesだけ）は、必須の部品1つとして扱う
    $components += [pscustomobject]@{ Id = 'core'; Name = '本体'; Required = $true; Recommended = $false
        Folders = @($rules.folders | ForEach-Object { $_.ToLowerInvariant() }); FlatFolders = @{}; Files = @($rules.files) }
}
$coreComponent = @($components | Where-Object { $_.Required })[0]
if (-not $coreComponent) { throw "$rulesPath に必須（required）の部品がありません" }
foreach ($c in $components) {
    if ($c.Id -notmatch '^[A-Za-z0-9_.+-]+$') { throw "部品のidに使えない文字があります: $($c.Id)" }
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
        throw "simutrans本体のexeを決められません: $src（exeのフルパスを指定してください）"
    }
}
$manifestDir = Split-Path -Parent $manifestPath
if (-not $dst.StartsWith($manifestDir + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "-Destinationはサーバーリストと同じフォルダの下にしてください: $dst"
}

# --- 1. 配るファイルを部品ごとに集める---
$exeName = Split-Path -Leaf $exe
$files = @([pscustomobject]@{ Rel = $exeName; File = (Get-Item -LiteralPath $exe); Component = $coreComponent.Id })
# 配らなかったものは、理由ごと（設定に書いていない／絶対に配らない）・フォルダとファイルごとに分けて表示する
$skippedFolders = @()
$skippedFiles = @()
$skippedInside = @()
$neverFolderItems = @()
$neverFileItems = @()
foreach ($item in Get-ChildItem -LiteralPath $src -Force) {
    if (Test-SkipOsFile $item $src) { continue }
    if ($item.PSIsContainer) {
        if ($NeverFolders -contains $item.Name.ToLowerInvariant()) { $neverFolderItems += $item.Name; continue }
        # 部品に書いてあるフォルダは配る。themes には見た目の画像として .pak が入っているので、
        # .pak の有無で pakset と決めつけるのは、部品に書いていないフォルダだけにする
        $flatOwner = @($components | Where-Object { $_.FlatFolders.ContainsKey($item.Name.ToLowerInvariant()) })[0]
        if ($flatOwner) {
            # 直下の、名前が合うファイルだけ（themes の *.tab と *.pak など）。中のフォルダやほかのファイルは配らない
            $wantedPatterns = $flatOwner.FlatFolders[$item.Name.ToLowerInvariant()]
            $leftOut = 0
            foreach ($f in Get-ChildItem -LiteralPath $item.FullName -Force) {
                if (-not $f.PSIsContainer -and -not (Test-SkipOsFile $f $src) -and (Test-Like $f.Name $wantedPatterns) -and -not (Test-Like $f.Name $NeverFiles)) {
                    $files += [pscustomobject]@{ Rel = "$($item.Name)/$($f.Name)"; File = $f; Component = $flatOwner.Id }
                }
                else { $leftOut++ }
            }
            if ($leftOut -gt 0) { $skippedInside += "$($item.Name)の中の$($wantedPatterns -join '・')以外のもの $leftOut 件" }
            continue
        }
        $owner = @($components | Where-Object { $_.Folders -contains $item.Name.ToLowerInvariant() })[0]
        if (-not $owner) {
            $isPakset = $null -ne (Get-ChildItem -LiteralPath $item.FullName -Filter '*.pak' -File -Force | Select-Object -First 1)
            if ($isPakset) { $neverFolderItems += "$($item.Name)（pakset）" } else { $skippedFolders += $item.Name }
            continue
        }
        foreach ($f in Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Force) {
            if (Test-SkipOsFile $f $src) { continue }
            # フォルダの中でも、スクリプトやexeなどは配らない
            if (Test-Like $f.Name $NeverFiles) { $neverFileItems += $f.FullName.Substring($src.Length + 1).Replace('\', '/'); continue }
            $files += [pscustomobject]@{ Rel = $f.FullName.Substring($src.Length + 1).Replace('\', '/'); File = $f; Component = $owner.Id }
        }
    }
    elseif ($item.Name -ne $exeName) {
        $isDll = $item.Extension -ieq '.dll'
        if (-not $isDll -and (Test-Like $item.Name $NeverFiles)) { $neverFileItems += $item.Name; continue }
        $owner = @($components | Where-Object { Test-Like $item.Name $_.Files })[0]
        if ($owner) { $files += [pscustomobject]@{ Rel = $item.Name; File = $item; Component = $owner.Id } }
        else { $skippedFiles += $item.Name }
    }
}
$files = @($files | Sort-Object -Property Rel -CaseSensitive)

function Write-NameList([string] $label, [string[]] $names, [int] $max = 10) {
    if ($names.Count -eq 0) { return }
    $shown = @($names | Select-Object -First $max)
    $more = if ($names.Count -gt $shown.Count) { " ほか $($names.Count - $shown.Count)件" } else { '' }
    Write-Host ("     {0}: {1}{2}" -f $label, ($shown -join ', '), $more)
}

Write-Host "   配るものの設定: $rulesName"
Write-Host "   配るもの"
foreach ($c in $components) {
    $mine = @($files | Where-Object { $_.Component -eq $c.Id })
    $kind = if ($c.Required) { '必須' } elseif ($c.Recommended) { '推奨' } else { '任意' }
    # Measure-Object -Property { ... } はWindows PowerShell 5.1では使えないので、自分で足す
    $size = [long]0
    foreach ($f in $mine) { $size += $f.File.Length }
    Write-Host ("     部品「{0}」（{1}）: {2} ファイル、{3:N0} バイト" -f $c.Name, $kind, $mine.Count, [long]$size)
}
if ($skippedFolders.Count + $skippedFiles.Count + $skippedInside.Count -gt 0) {
    Write-Host "   配らないもの（設定に書いていないため。配りたいときはengine-files.jsonの部品に足す）"
    Write-NameList 'フォルダ' $skippedFolders
    Write-NameList 'ファイル' $skippedFiles
    foreach ($inside in $skippedInside) { Write-Host "     $inside" }
}
if ($neverFolderItems.Count + $neverFileItems.Count -gt 0) {
    Write-Host "   絶対に配らないもの（設定にかかわらず。セーブデータ、スクリプト、ほかのexe、paksetなど）"
    Write-NameList 'フォルダ' $neverFolderItems
    Write-NameList 'ファイル' $neverFileItems
}

# --- 2. 識別名（中身と部品の分け方が同じなら同じ名前になる）---
$hashes = @{}
$lines = foreach ($f in $files) {
    $h = (Get-FileHash -LiteralPath $f.File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashes[$f.Rel] = $h
    "{0}|{1}|{2}|{3}" -f $f.Rel, $f.File.Length, $h, $f.Component
}
$lines += @($components | ForEach-Object { "component|{0}|{1}|{2}|{3}" -f $_.Id, $_.Name, $_.Required, $_.Recommended })
$sha = [System.Security.Cryptography.SHA256]::Create()
$fingerprint = ([System.BitConverter]::ToString($sha.ComputeHash($Utf8NoBom.GetBytes(($lines -join "`n")))) -replace '-', '').ToLowerInvariant()
# 版の名前はexeの名前にする（OTRPのsim-WinGDI64-OTRPv62_0_3.exeなどは、exeに書かれた版が元のsimutransの版
# 「122.0.1 Nightly」になっていて、どの本体か分からないため）。
# 名前がsimutrans.exeだけで区別できない場合は、exeに書かれた版を足す
$version = [System.IO.Path]::GetFileNameWithoutExtension($exe)
if ($version -eq 'simutrans') {
    $info = (Get-Item -LiteralPath $exe).VersionInfo
    $exeVersion = if ($info.ProductVersion) { $info.ProductVersion } else { $info.FileVersion }
    if ($exeVersion) { $version = "simutrans-$exeVersion" }
}
$version = ($version -replace '[^A-Za-z0-9_.-]+', '-').Trim('-', '.')
$revision = if ($version) { "$version-$($fingerprint.Substring(0, 8))" } else { "r-$($fingerprint.Substring(0, 8))" }

# --- 3. ファイルとファイル一覧を置く---
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
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Move-Item -LiteralPath $staging -Destination $revDir
}
# IISで配信できるようにする（前回と同じ版でも、設定を直したときのために毎回書く）
Write-DistWebConfig (Join-Path $revDir 'web.config') @($files | ForEach-Object { $_.File.Extension.ToLowerInvariant() }) `
    'Generated by Publish-Engine.ps1. simutrans本体のファイルをIISから配信できるようにする。'
$indexPath = Join-Path $revDir 'index.json'
$indexSha = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash.ToLowerInvariant()
$indexUrl = $indexPath.Substring($manifestDir.Length + 1).Replace('\', '/')

$data = Read-JsonFile $manifestPath
foreach ($id in $ServerId) {
    $server = @($data.servers | Where-Object { $_.id -eq $id })
    if ($server.Count -ne 1) { throw "サーバーリストにidが '$id' のサーバーが見つかりません" }
    $engine = [ordered]@{
        revision = $revision
        builds   = [ordered]@{ 'windows-x64' = [ordered]@{ index_url = $indexUrl; index_sha256 = $indexSha; exe = $exeName } }
    }
    $server[0] | Add-Member -NotePropertyName engine -NotePropertyValue $engine -Force
}
$data | Add-Member -NotePropertyName updated_at -NotePropertyValue (Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz') -Force
Write-ManifestFile $manifestPath $data

# --- 4. どのサーバーも使わなくなった古い版を消す---
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

$total = [long]0
foreach ($f in $files) { $total += $f.File.Length }
$state = if ($created) { '公開しました' } else { '前回と同じなので置き直していません' }
Write-Host ("本体を公開しました: {0}（{1} ファイル、{2:N0} バイト、{3}）" -f $revision, $files.Count, [long]$total, $state)
Write-Host "サーバーリストのengineを更新しました（$($ServerId -join ', ')、revision $revision）"
