<#
.SYNOPSIS
    simutrans サーバーの本体（Windows 版）を zip にして公開し、サーバーリストに書く。

.DESCRIPTION
    友人のランチャーは、サーバーと同じ本体を自動で入れて起動する（本体が違うとチェックサムがずれることがあるため）。
    1. -Source（simutrans 本体の exe）があるフォルダのファイルを集める
       pakset のフォルダ（直下に .pak があるフォルダ）、save / screenshot / addons / maps フォルダ、
       セーブデータ（.sve）やログは含めない。フォルダ直下は本体の exe と .dll だけにする
       （ほかの版の exe、makeobj / nettool、json やバッチファイルなどのサーバー用のファイルは配らない）
    2. 中身から識別名（revision）を決め、前回と同じなら zip を作り直さない
    3. -Destination に zip を置き、サーバーリストの該当サーバーの engine を書き換える
    4. どのサーバーも使わなくなった古い zip を消す

    ランチャーは HTTPS で取得したサーバーリストからしか本体を入れない（通信途中ですり替えられないようにするため）。
    Enable-Https.ps1 で HTTPS にしておくこと。

    引数なしの Publish-Pakset.ps1（Publish-Pakset.bat）から自動で呼ばれる。
#>
[CmdletBinding()]
param(
    # simutrans 本体の exe のフルパス（フォルダを渡すと中の exe を探す）
    [Parameter(Mandatory = $true)] [string] $Source,
    # zip を置くフォルダ（サーバーリストと同じフォルダかその下）
    [Parameter(Mandatory = $true)] [string] $Destination,
    # 書き換えるサーバーリスト（manifest.json）
    [Parameter(Mandatory = $true)] [string] $Manifest,
    # この本体を使うサーバーの id
    [Parameter(Mandatory = $true)] [string[]] $ServerId
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$ExcludedDirs = @('save', 'screenshot', 'addons', 'maps')
$ExcludedExtensions = @('.sve', '.log', '.tmp', '.bak')
$ExcludedFiles = @('settings.xml')

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

# --- 1. 含めるファイルを集める ---
$skipDirs = @()
foreach ($d in Get-ChildItem -LiteralPath $src -Directory -Force) {
    $isPakset = $null -ne (Get-ChildItem -LiteralPath $d.FullName -Filter '*.pak' -File -Force | Select-Object -First 1)
    if ($isPakset -or ($ExcludedDirs -contains $d.Name.ToLowerInvariant())) {
        $skipDirs += $d.FullName + [System.IO.Path]::DirectorySeparatorChar
    }
}
$files = @()
foreach ($f in Get-ChildItem -LiteralPath $src -Recurse -File -Force) {
    $skip = $false
    foreach ($d in $skipDirs) { if ($f.FullName.StartsWith($d, [System.StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break } }
    if ($skip) { continue }
    if ($ExcludedExtensions -contains $f.Extension.ToLowerInvariant()) { continue }
    $rel = $f.FullName.Substring($src.Length + 1).Replace('\', '/')
    if ($ExcludedFiles -contains $rel.ToLowerInvariant()) { continue }
    # フォルダ直下は、本体の exe と .dll だけにする（ほかの版の exe、json やバッチファイルなどのサーバー用のファイルは配らない）
    if ($rel -notmatch '/' -and $f.FullName -ne $exe -and $f.Extension -ine '.dll') { continue }
    $files += [pscustomobject]@{ Rel = $rel; File = $f }
}
$files = @($files | Sort-Object -Property Rel -CaseSensitive)

# --- 2. 識別名（中身が同じなら同じ名前になる） ---
$lines = foreach ($f in $files) {
    "{0}|{1}|{2}" -f $f.Rel, $f.File.Length, (Get-FileHash -LiteralPath $f.File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
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

# --- 3. zip を作って公開する ---
New-Item -ItemType Directory -Force -Path $dst | Out-Null
$zipName = "simutrans-$revision-windows-x64.zip"
$zipPath = Join-Path $dst $zipName
$created = $false
if (-not (Test-Path -LiteralPath $zipPath)) {
    $tmp = "$zipPath.tmp"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($tmp, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $files) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.File.FullName, $f.Rel, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
    Move-Item -LiteralPath $tmp -Destination $zipPath -Force
    $created = $true
}
$zipSha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipUrl = $zipPath.Substring($manifestDir.Length + 1).Replace('\', '/')
$exeRel = $exe.Substring($src.Length + 1).Replace('\', '/')

$data = Read-JsonFile $manifestPath
foreach ($id in $ServerId) {
    $server = @($data.servers | Where-Object { $_.id -eq $id })
    if ($server.Count -ne 1) { throw "サーバーリストに id が '$id' のサーバーが見つかりません" }
    $engine = [ordered]@{
        revision = $revision
        builds   = [ordered]@{ 'windows-x64' = [ordered]@{ url = $zipUrl; sha256 = $zipSha; exe = $exeRel } }
    }
    $server[0] | Add-Member -NotePropertyName engine -NotePropertyValue $engine -Force
}
$data | Add-Member -NotePropertyName updated_at -NotePropertyValue (Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz') -Force
Write-JsonFile $manifestPath $data

# --- 4. どのサーバーも使わなくなった古い zip を消す ---
$data = Read-JsonFile $manifestPath
$inUse = @($data.servers | Where-Object { $_.PSObject.Properties['engine'] -and $_.engine } | ForEach-Object {
    $b = $_.engine.builds.PSObject.Properties['windows-x64']
    if ($b) { $b.Value.url }
})
foreach ($old in Get-ChildItem -LiteralPath $dst -Filter 'simutrans-*-windows-x64.zip' -File) {
    $url = $old.FullName.Substring($manifestDir.Length + 1).Replace('\', '/')
    if (-not ($inUse -contains $url)) { Remove-Item -LiteralPath $old.FullName -Force }
}

$size = (Get-Item -LiteralPath $zipPath).Length
$state = if ($created) { '作りました' } else { '前回と同じなので作り直していません' }
Write-Host ("本体を公開しました: {0}（{1} ファイル、{2:N0} バイト、{3}）" -f $zipName, $files.Count, $size, $state)
Write-Host "サーバーリストの engine を更新しました（$($ServerId -join ', ')、revision $revision）"
