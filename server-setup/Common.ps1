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
  engine_source は simutrans 本体の exe のフルパス（ふつうは pakset フォルダの1つ上にある）。なければ本体は公開しない。
  以前の版ではフォルダを入れていたので、フォルダが入っていたら中の exe を探す。
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

# simutrans と一緒に置かれることが多い、本体ではない exe
$NotEngineExe = '^(makeobj|nettool|unins|uninstall|setup|update|vc_?redist)'

# フォルダ直下の exe のうち、本体の候補（makeobj などを除く）。新しい順
function Get-ExeCandidates([string] $dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Filter '*.exe' -File | Where-Object { $_.Name -notmatch $NotEngineExe } |
        Sort-Object LastWriteTime -Descending)
}

# 指定したポートで simutrans サーバー（-server <port>）として動いているプロセスの exe を探す
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

# simutrans 本体の実行ファイルを名前から探す。
# simutrans.exe → simutrans*.exe / sim-*.exe（OTRP の sim-WinGDI64-OTRPv57.exe など）が1つだけ → 候補が1つだけ。決められなければ $null
function Find-SimutransExe([string] $dir) {
    $exes = @(Get-ExeCandidates $dir)
    $main = @($exes | Where-Object { $_.Name -ieq 'simutrans.exe' })
    if ($main.Count -gt 0) { return $main[0].FullName }
    $named = @($exes | Where-Object { $_.Name -match '^(simutrans|sim-).*\.exe$' })
    if ($named.Count -eq 1) { return $named[0].FullName }
    if ($exes.Count -eq 1) { return $exes[0].FullName }
    return $null
}

# 本体の exe を決める。動いているサーバーの exe → 名前 → 番号で選んでもらう（空欄なら本体は配らない）
function Resolve-EngineExe([string] $paksetSource, [int[]] $ports) {
    $dir = Split-Path -Parent $paksetSource
    $running = Find-RunningServerExe $dir $ports
    if ($running) {
        Write-Ok "動いている simutrans サーバーの本体を使います: $(Split-Path -Leaf $running)"
        return $running
    }
    $exe = Find-SimutransExe $dir
    if ($exe) { return $exe }
    $candidates = @(Get-ExeCandidates $dir)
    if ($candidates.Count -eq 0) {
        Write-Warning "$dir に simutrans 本体の exe が見つかりません。本体は配りません"
        return $null
    }
    Write-Host "   $dir に exe が複数あります。simutrans サーバーの起動に使っている本体を選んでください（新しい順）"
    for ($i = 0; $i -lt $candidates.Count; $i++) {
        Write-Host ("     {0}. {1}（{2:yyyy/MM/dd}）" -f ($i + 1), $candidates[$i].Name, $candidates[$i].LastWriteTime)
    }
    while ($true) {
        $answer = (Read-Host "番号（空欄なら本体は配らない）").Trim()
        if (-not $answer) { return $null }
        $n = 0
        if ([int]::TryParse($answer, [ref]$n) -and $n -ge 1 -and $n -le $candidates.Count) { return $candidates[$n - 1].FullName }
        Write-Warning "1〜$($candidates.Count) の番号で答えてください"
    }
}

# pakset フォルダの1つ上に simutrans 本体が見つかれば、その exe のフルパスを返す（質問はしない）
function Get-EngineSource([string] $paksetSource) {
    return Find-SimutransExe (Split-Path -Parent $paksetSource)
}

# サーバーリストへの署名
. (Join-Path $PSScriptRoot 'Signing.ps1')
