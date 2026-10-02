# server-setupのスクリプトで共通に使う関数。各スクリプトから. "$PSScriptRoot\Common.ps1" で読み込む。
# Windows PowerShell 5.1はBOMがないと日本語を正しく読めないので、BOM付きUTF-8で保存すること。

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
  Publish-Pakset.batが使う設定（publish-settings.json）。
  {
    "manifest": "C:\\simutrans-dist\\manifest.json",
    "paksets": [ { "pakset_source": "...", "destination": "...", "server_ids": ["friends-a"], "engine_source": "..." } ]
  }
  同じpaksetフォルダを使うサーバーは、1つの項目のserver_idsにまとめる。
  engine_sourceはsimutrans本体のexeのフルパス（ふつうはpaksetフォルダの1つ上にある）。なければ本体は公開しない。
  以前の版ではフォルダを入れていたので、フォルダが入っていたら中のexeを探す。
#>
function Get-PublishSettings {
    $result = [ordered]@{ manifest = $null; share_url = $null; paksets = @() }
    if (-not (Test-Path -LiteralPath $PublishSettingsPath)) {
        return $result
    }
    $saved = Read-JsonFile $PublishSettingsPath
    $result.manifest = $saved.manifest
    if ($saved.PSObject.Properties['share_url']) { $result.share_url = $saved.share_url }
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
    Write-JsonFile $PublishSettingsPath ([ordered]@{ manifest = $settings.manifest; share_url = $settings.share_url; paksets = @($settings.paksets) })
}

# 推測されにくい名前に使う、英小文字と数字のランダムな文字列
function New-RandomName([int] $length = 10) {
    $chars = 'abcdefghijkmnpqrstuvwxyz23456789'
    $bytes = New-RandomBytes $length
    return -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

<#
  サーバーリストのファイルの場所。
  1. publish-settings.json に登録してあり、そのファイルが公開フォルダにあれば、それを使う
  2. 以前の版で作った manifest.json があれば、それを使う（今動いている環境はそのまま）
  3. どちらもなければ、推測されにくい名前（list-ランダム.json）を決めて設定に残す。
     新規構築では、続けて呼ばれるスクリプトもこの名前を使う
  名前を変えたいときは Rename-ServerList.bat を使う。
#>
function Resolve-ManifestPath([string] $distDir) {
    $dist = Get-FullPath $distDir
    $settings = Get-PublishSettings
    $saved = if ($settings.manifest -and (Split-Path -Parent (Get-FullPath $settings.manifest)) -eq $dist) {
        Get-FullPath $settings.manifest
    }
    if ($saved -and (Test-Path -LiteralPath $saved)) { return $saved }
    $legacy = Join-Path $dist 'manifest.json'
    if (Test-Path -LiteralPath $legacy) { return $legacy }
    $existing = @(Get-ChildItem -LiteralPath $dist -Filter 'list-*.json' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.sig.json' })
    if ($existing.Count -gt 0) { return $existing[0].FullName }
    # 名前を決めたあと、まだファイルを作る前（新規構築の途中）
    if ($saved) { return $saved }
    # 新しい名前は設定に残し、このあと呼ばれたときも同じ名前を返す
    $path = Join-Path $dist "list-$(New-RandomName 10).json"
    $settings.manifest = $path
    Save-PublishSettings $settings
    return $path
}

# 公開アドレス（友人に伝えるURL）を、サーバーリストのファイル名に合わせて作る
function Get-ShareUrl([string] $baseUrl, [string] $manifestPath) {
    return ($baseUrl.TrimEnd('/') + '/' + (Split-Path -Leaf $manifestPath))
}

# paksetを登録する。同じpaksetフォルダが登録済みなら、そこにサーバーのidを足す。
# 本体のフォルダ（paksetフォルダの1つ上）にsimutrans.exeがあれば、本体も公開の対象にする
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

# simutransと一緒に置かれることが多い、本体ではないexe
$NotEngineExe = '^(makeobj|nettool|unins|uninstall|setup|update|vc_?redist)'

# フォルダ直下のexeのうち、本体の候補（makeobjなどを除く）。新しい順
function Get-ExeCandidates([string] $dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Filter '*.exe' -File | Where-Object { $_.Name -notmatch $NotEngineExe } |
        Sort-Object LastWriteTime -Descending)
}

# 指定したポートでsimutransサーバー（-server <port>）として動いているプロセスのexeを探す
function Find-RunningServerExe([string] $dir, [int[]] $ports) {
    if (-not $dir -or -not $ports -or -not (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) { return $null }
    try {
        $prefix = (Get-FullPath $dir) + [System.IO.Path]::DirectorySeparatorChar
        foreach ($proc in Get-CimInstance Win32_Process -ErrorAction Stop) {
            if (-not $proc.ExecutablePath -or -not $proc.CommandLine) { continue }
            if (-not $proc.ExecutablePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($proc.CommandLine -match '-server\s+(\d+)' -and ($ports -contains [int]$Matches[1])) { return $proc.ExecutablePath }
        }
    }
    catch { }
    return $null
}

# simutrans本体の実行ファイルを名前から探す。
# simutrans.exe→simutrans*.exe / sim-*.exe（OTRPのsim-WinGDI64-OTRPv57.exeなど）が1つだけ → 候補が1つだけ。決められなければ $null
function Find-SimutransExe([string] $dir) {
    $exes = @(Get-ExeCandidates $dir)
    $main = @($exes | Where-Object { $_.Name -ieq 'simutrans.exe' })
    if ($main.Count -gt 0) { return $main[0].FullName }
    $named = @($exes | Where-Object { $_.Name -match '^(simutrans|sim-).*\.exe$' })
    if ($named.Count -eq 1) { return $named[0].FullName }
    if ($exes.Count -eq 1) { return $exes[0].FullName }
    return $null
}

# 本体のexeを決める。動いているサーバーのexe→ 名前 → 番号で選んでもらう（空欄なら本体は配らない）
function Resolve-EngineExe([string] $paksetSource, [int[]] $ports) {
    $dir = Split-Path -Parent $paksetSource
    $running = Find-RunningServerExe $dir $ports
    if ($running) {
        Write-Ok "動いているsimutransサーバーの本体を使います: $(Split-Path -Leaf $running)"
        return $running
    }
    $exe = Find-SimutransExe $dir
    if ($exe) { return $exe }
    $candidates = @(Get-ExeCandidates $dir)
    if ($candidates.Count -eq 0) {
        Write-Warning "$dir にsimutrans本体のexeが見つかりません。本体は配りません"
        return $null
    }
    Write-Host "   $dir にexeが複数あります。simutransサーバーの起動に使っている本体を選んでください（新しい順）"
    for ($i = 0; $i -lt $candidates.Count; $i++) {
        Write-Host ("     {0}. {1}（{2:yyyy/MM/dd}）" -f ($i + 1), $candidates[$i].Name, $candidates[$i].LastWriteTime)
    }
    while ($true) {
        $answer = (Read-Host "番号（空欄なら本体は配らない）").Trim()
        if (-not $answer) { return $null }
        $n = 0
        if ([int]::TryParse($answer, [ref]$n) -and $n -ge 1 -and $n -le $candidates.Count) { return $candidates[$n - 1].FullName }
        Write-Warning "1〜$($candidates.Count)の番号で答えてください"
    }
}

# paksetフォルダの1つ上にsimutrans本体が見つかれば、そのexeのフルパスを返す（質問はしない）
function Get-EngineSource([string] $paksetSource) {
    return Find-SimutransExe (Split-Path -Parent $paksetSource)
}

# サーバーリストへの署名
. (Join-Path $PSScriptRoot 'Signing.ps1')

# IISで配信するフォルダのweb.configを書く。
# -使っている拡張子を登録する（IISは知らない拡張子のファイルを配らないため。.pak・.tab・.bdfなど）
# -ファイル名の「+」を通す（m+10r.bdfなど。IISは標準では「+」を含むアドレスを断る）。
#   ファイルをそのまま配るだけのサイトなので、通しても危険はない
function Write-DistWebConfig([string] $path, [string[]] $extensions, [string] $comment) {
    $maps = (@($extensions | Where-Object { $_ -and $_ -ne '.json' } | Sort-Object -Unique) | ForEach-Object {
        "      <remove fileExtension=`"$_`" />`n      <mimeMap fileExtension=`"$_`" mimeType=`"application/octet-stream`" />"
    }) -join "`n"
    Write-TextFile $path @"
<?xml version="1.0" encoding="utf-8"?>
<!-- $comment -->
<configuration>
  <system.webServer>
    <staticContent>
$maps
    </staticContent>
    <security>
      <requestFiltering allowDoubleEscaping="true" />
    </security>
  </system.webServer>
</configuration>
"@
}

# Windows などが自動で作るファイル（desktop.ini や Thumbs.db）と、隠しファイル・システムファイルは配らない。
# 遊ぶのに要らないうえ、IIS は隠しファイルを配信しないので、ランチャーの同期が 404 で止まってしまうため。
# 隠しフォルダの中のファイルも同じ扱いにする
$OsJunkNames = @('desktop.ini', 'thumbs.db', 'ehthumbs.db', '.ds_store')
function Test-SkipOsFile($file, [string] $root) {
    if ($OsJunkNames -contains $file.Name.ToLowerInvariant()) { return $true }
    $hiddenOrSystem = [System.IO.FileAttributes]::Hidden -bor [System.IO.FileAttributes]::System
    $item = $file
    $rootFull = $root.TrimEnd('\', '/')
    while ($item -and $item.FullName.TrimEnd('\', '/') -ne $rootFull) {
        if ($item.Attributes -band $hiddenOrSystem) { return $true }
        # PSIsContainer は Get-ChildItem が付ける情報で、.Directory などでたどったものには付かない（Windows PowerShell 5.1）
    $item = if ($item -is [System.IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
    return $false
}
