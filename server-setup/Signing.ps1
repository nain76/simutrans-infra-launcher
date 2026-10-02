# サーバーリスト（manifest.json）への署名。Common.ps1から読み込まれる。
#
# しくみ（エンジニアでなくても分かるように）:
#   -「秘密の鍵」はこのVPSの中だけに置く。これでmanifest.jsonに「管理者が公開したもの」という印（署名）を付ける
#   -印はmanifest.sig.jsonとしてmanifest.jsonの隣に置く。友人のランチャーは毎回これを確かめる
#   -友人には最初に一度だけ「確認コード」（秘密の鍵と対になる公開鍵から作る短い文字列）を伝える。
#     確認コードは秘密ではない。友人は聞いたコードをランチャーに入力するだけでよい
#   -配信フォルダを誰かに書き換えられても、秘密の鍵がなければ正しい印は作れないので、ランチャーが気づいて止める
#   - paksetと本体のファイル一覧はmanifest.jsonにSHA256が書いてあるので、manifest.jsonの印だけで全部を守れる
#
# 秘密の鍵のファイルは%LOCALAPPDATA%\InfraLauncherServer\signing-key.datに置き、WindowsのDPAPIで
# 「このWindowsユーザーでしか開けない」ように暗号化する（配信フォルダには置かない）。
# 形式はECDSA P-256 / SHA-256。Windows PowerShell 5.1（.NET Framework 4.7以降）とPowerShell 7の両方で動く。

$SigningKeyPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'InfraLauncherServer\signing-key.dat'
$SigningKeyFormat = 'infra-launcher-signing-key-1'
$SignatureFormat = 'infra-launcher-signature-1'
$BackupFormat = 'infra-launcher-signing-key-backup-1'
$DpapiEntropy = [System.Text.Encoding]::UTF8.GetBytes('InfraLauncher.ManifestSigning')

# P-256の公開鍵をSubjectPublicKeyInfo（DER）にするときの先頭部分。後ろに0x04・X・Yを続ける
$P256SpkiPrefix = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01,
    0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)

if (-not ('System.Security.Cryptography.ProtectedData' -as [type])) {
    try { Add-Type -AssemblyName System.Security } catch { }
}

function Join-Bytes([byte[]] $a, [byte[]] $b) {
    $result = New-Object byte[] ($a.Length + $b.Length)
    [Array]::Copy($a, 0, $result, 0, $a.Length)
    [Array]::Copy($b, 0, $result, $a.Length, $b.Length)
    return , $result
}

function Get-ByteRange([byte[]] $bytes, [int] $start, [int] $count) {
    $result = New-Object byte[] $count
    [Array]::Copy($bytes, $start, $result, 0, $count)
    return , $result
}

function New-RandomBytes([int] $count) {
    $bytes = New-Object byte[] $count
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    return , $bytes
}

function Test-IsWindowsHost {
    return -not (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue) -or $IsWindows
}

# DPAPIで「このWindowsユーザーでしか開けない」ようにする。
# Windows以外（開発時の動作確認）ではINFRA_SIGNING_INSECURE_TEST=1のときだけ暗号化せずに保存する
function Protect-KeyBytes([byte[]] $bytes) {
    if (Test-IsWindowsHost) {
        return 'dpapi:' + [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect($bytes, $DpapiEntropy, 'CurrentUser'))
    }
    if ($env:INFRA_SIGNING_INSECURE_TEST -ne '1') { throw '署名の鍵はWindowsでしか保存できません' }
    return 'plain:' + [Convert]::ToBase64String($bytes)
}

function Unprotect-KeyBytes([string] $text) {
    $kind, $data = $text -split ':', 2
    if ($kind -eq 'dpapi') {
        return , [System.Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($data), $DpapiEntropy, 'CurrentUser')
    }
    if ($kind -eq 'plain' -and $env:INFRA_SIGNING_INSECURE_TEST -eq '1') { return , [Convert]::FromBase64String($data) }
    throw '署名の鍵のファイルの形式が正しくありません'
}

function Get-P256Curve { return [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256 }

# 以前の版が名前の空の鍵としてWindowsに残してしまった鍵を消す（秘密の鍵の写しを残さないため）
function Remove-StrayCngKey {
    try {
        if ([System.Security.Cryptography.CngKey]::Exists('')) {
            [System.Security.Cryptography.CngKey]::Open('').Delete()
            Write-Host '   以前の版がWindowsの鍵の保管場所に残した鍵の写しを消しました'
        }
    }
    catch { }
}

# 鍵を新しく作り、秘密の値（D・X・Yを並べた96バイト）を返す
function New-KeyBytes {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        # .NET Frameworkでは、取り出しを許した鍵として作る必要がある
        $params = New-Object System.Security.Cryptography.CngKeyCreationParameters
        $params.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
        # 名前に$nullを渡すとPowerShellが空文字に変え、名前付きの鍵としてWindowsに残ってしまう。
        # [NullString]::Valueで本当のnullを渡し、その場限りの鍵として作る
        $cng = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $params)
        $ec = New-Object System.Security.Cryptography.ECDsaCng($cng)
        Remove-StrayCngKey
    }
    else {
        $ec = [System.Security.Cryptography.ECDsa]::Create((Get-P256Curve))
    }
    try {
        $p = $ec.ExportParameters($true)
        if ($p.D.Length -ne 32 -or $p.Q.X.Length -ne 32 -or $p.Q.Y.Length -ne 32) { throw '鍵を作れませんでした' }
        return , (Join-Bytes (Join-Bytes $p.D $p.Q.X) $p.Q.Y)
    }
    finally { $ec.Dispose() }
}

function New-EcdsaFromKeyBytes([byte[]] $raw) {
    $q = New-Object System.Security.Cryptography.ECPoint
    $q.X = Get-ByteRange $raw 32 32
    $q.Y = Get-ByteRange $raw 64 32
    $p = New-Object System.Security.Cryptography.ECParameters
    $p.Curve = Get-P256Curve
    $p.Q = $q
    $p.D = Get-ByteRange $raw 0 32
    return [System.Security.Cryptography.ECDsa]::Create($p)
}

# 公開鍵（SubjectPublicKeyInfoのbase64）。ランチャーのManifestSignatureと同じ形
function Get-PublicKeyText([byte[]] $raw) {
    return [Convert]::ToBase64String((Join-Bytes $P256SpkiPrefix (Get-ByteRange $raw 32 64)))
}

# 確認コード: 公開鍵のSHA256の先頭10バイトを16進数で4文字ずつ区切ったもの（ランチャーと同じ計算）
function Get-SigningCode([string] $publicKey) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hash = $sha.ComputeHash([Convert]::FromBase64String($publicKey))
    $sha.Dispose()
    $hex = -join ($hash[0..9] | ForEach-Object { $_.ToString('X2') })
    return ($hex -split '(.{4})' | Where-Object { $_ }) -join '-'
}

function Get-SigningKey {
    if (-not (Test-Path -LiteralPath $SigningKeyPath)) { return $null }
    $key = Read-JsonFile $SigningKeyPath
    if ($key.format -ne $SigningKeyFormat) { throw "署名の鍵のファイルの形式が正しくありません: $SigningKeyPath" }
    return [pscustomobject]@{ PublicKey = $key.public_key; Code = (Get-SigningCode $key.public_key); User = $key.windows_user; Protected = $key.protected }
}

function Save-SigningKey([byte[]] $raw) {
    $dir = Split-Path -Parent $SigningKeyPath
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Write-JsonFile $SigningKeyPath ([ordered]@{
        format       = $SigningKeyFormat
        public_key   = Get-PublicKeyText $raw
        protected    = Protect-KeyBytes $raw
        windows_user = [Environment]::UserName
        created_at   = Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz'
    })
}

function Get-SigningKeyBytes {
    $key = Get-SigningKey
    if (-not $key) { throw '署名の鍵がありません。Manage-SigningKey.batで作ってください' }
    try { $raw = Unprotect-KeyBytes $key.Protected }
    catch {
        throw "署名の鍵を開けませんでした。鍵を作ったWindowsユーザー（$($key.User)）で実行してください。VPSを作り直した場合はManage-SigningKey.batの「バックアップから戻す」を使ってください（$($_.Exception.Message)）"
    }
    if ((Get-PublicKeyText $raw) -ne $key.PublicKey) { throw "署名の鍵のファイルが壊れています: $SigningKeyPath" }
    return , $raw
}

function Get-SignaturePath([string] $manifestPath) {
    if ($manifestPath -match '\.json$') { return ($manifestPath -replace '\.json$', '.sig.json') }
    return "$manifestPath.sig.json"
}

# いま配信している署名の確認コード（署名がなければ $null）
function Get-PublishedSigningCode([string] $manifestPath) {
    $sigPath = Get-SignaturePath $manifestPath
    if (-not (Test-Path -LiteralPath $sigPath)) { return $null }
    try { return Get-SigningCode (Read-JsonFile $sigPath).public_key } catch { return $null }
}

# manifest.jsonに署名してmanifest.sig.jsonを書く（manifest.jsonはバイト列そのものに署名するので、このあと書き換えないこと）
function Invoke-ManifestSign([string] $manifestPath) {
    $raw = Get-SigningKeyBytes
    $ec = New-EcdsaFromKeyBytes $raw
    try {
        $bytes = [System.IO.File]::ReadAllBytes($manifestPath)
        $signature = $ec.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally { $ec.Dispose() }
    Write-JsonFile (Get-SignaturePath $manifestPath) ([ordered]@{
        format     = $SignatureFormat
        public_key = Get-PublicKeyText $raw
        signature  = [Convert]::ToBase64String($signature)
        signed_at  = Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz'
    })
}

function Show-SigningCode([string] $code) {
    Write-Host ''
    Write-Host '   ┌──────────────────────────────────────┐' -ForegroundColor Yellow
    Write-Host "      確認コード:  $code" -ForegroundColor Yellow
    Write-Host '   └──────────────────────────────────────┘' -ForegroundColor Yellow
    Write-Host '   ・この確認コードを、DiscordのDMなどで友人に一度だけ伝えてください'
    Write-Host '     友人はランチャーでサーバーリストを追加するとき、このコードを入力します（ランチャーにはコードは表示されません）'
    Write-Host '   ・確認コードは秘密ではありません。人に見られても問題ありません（パスワードではありません）'
    Write-Host '   ・サーバーリストのアドレスと一緒に伝えてかまいません。ただし配信サーバー（8080 / 8443）に置いて伝えるのはやめてください'
}

function Write-SigningExplanation {
    Write-Host '   これは何？'
    Write-Host '    ・サーバーリスト（manifest.json）に「管理者が公開したもの」という印（署名）を付けるための鍵です'
    Write-Host '    ・印は、このVPSにある秘密の鍵でしか作れません'
    Write-Host '    ・友人のランチャーは毎回この印を確かめます。誰かが配信フォルダのファイルを書き換えても、'
    Write-Host '      印が合わなくなるので気づいて止まります（知らないexeを実行してしまうことを防ぎます）'
    Write-Host '    ・Publish-Pakset.batなどで公開するたびに、自動で印を付け直します。普段は何もしなくてかまいません'
}

function Read-Password([string] $prompt) {
    $secure = Read-Host $prompt -AsSecureString
    return (New-Object System.Net.NetworkCredential('', $secure)).Password
}

function Get-BackupKeys([string] $password, [byte[]] $salt, [int] $iterations) {
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($password, $salt, $iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $keys = $kdf.GetBytes(64)
    $kdf.Dispose()
    return @{ Enc = (Get-ByteRange $keys 0 32); Mac = (Get-ByteRange $keys 32 32) }
}

function Get-Hmac([byte[]] $key, [byte[]] $data) {
    $hmac = New-Object System.Security.Cryptography.HMACSHA256(, $key)
    $mac = $hmac.ComputeHash($data)
    $hmac.Dispose()
    return , $mac
}

# パスワード付きのバックアップを書く（PBKDF2-SHA256で鍵を作り、AES-256-CBCで暗号化してHMAC-SHA256を付ける）
function Export-SigningKeyBackup([string] $path, [string] $password) {
    $raw = Get-SigningKeyBytes
    $salt = New-RandomBytes 16
    $iv = New-RandomBytes 16
    $iterations = 300000
    $keys = Get-BackupKeys $password $salt $iterations
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Key = $keys.Enc
    $aes.IV = $iv
    $encryptor = $aes.CreateEncryptor()
    $data = $encryptor.TransformFinalBlock($raw, 0, $raw.Length)
    $encryptor.Dispose()
    $aes.Dispose()
    $mac = Get-Hmac $keys.Mac (Join-Bytes $iv $data)
    Write-JsonFile $path ([ordered]@{
        format     = $BackupFormat
        note       = 'InfraLauncherのサーバーリスト署名の鍵のバックアップ。戻すときはManage-SigningKey.batの「バックアップから戻す」を使う。パスワードがないと開けない'
        code       = Get-SigningCode (Get-PublicKeyText $raw)
        public_key = Get-PublicKeyText $raw
        kdf        = 'pbkdf2-sha256'
        iterations = $iterations
        salt       = [Convert]::ToBase64String($salt)
        iv         = [Convert]::ToBase64String($iv)
        data       = [Convert]::ToBase64String($data)
        mac        = [Convert]::ToBase64String($mac)
    })
}

function Import-SigningKeyBackup([string] $path, [string] $password) {
    $b = Read-JsonFile $path
    if ($b.format -ne $BackupFormat) { throw "署名の鍵のバックアップではありません: $path" }
    $iv = [Convert]::FromBase64String($b.iv)
    $data = [Convert]::FromBase64String($b.data)
    $keys = Get-BackupKeys $password ([Convert]::FromBase64String($b.salt)) ([int]$b.iterations)
    $mac = Get-Hmac $keys.Mac (Join-Bytes $iv $data)
    if ([Convert]::ToBase64String($mac) -ne $b.mac) { throw 'パスワードが違うか、バックアップのファイルが壊れています' }
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Key = $keys.Enc
    $aes.IV = $iv
    $decryptor = $aes.CreateDecryptor()
    $raw = $decryptor.TransformFinalBlock($data, 0, $data.Length)
    $decryptor.Dispose()
    $aes.Dispose()
    if ((Get-PublicKeyText $raw) -ne $b.public_key) { throw 'バックアップの中身が正しくありません' }
    Save-SigningKey $raw
}

# バックアップを作るかを聞いて作る
function Invoke-BackupPrompt {
    Write-Host ''
    Write-Host '   VPSを作り直したときも同じ確認コードを使い続けられるよう、パスワード付きのバックアップを作れます。'
    Write-Host '   作らない場合、VPSを作り直したら鍵を作り直すことになり、友人全員に新しい確認コードを入力し直してもらうことになります。'
    $answer = Read-Value 'バックアップを作りますか？（Y/n）' 'Y'
    if ($answer -notmatch '^[Yy]') { return }
    while ($true) {
        $p1 = Read-Password 'バックアップのパスワード（8文字以上。忘れると戻せません）'
        if ($p1.Length -lt 8) { Write-Warning '8文字以上にしてください'; continue }
        $p2 = Read-Password 'もう一度同じパスワード'
        if ($p1 -ne $p2) { Write-Warning 'パスワードが一致しません'; continue }
        break
    }
    $path = Resolve-BackupPath (Read-Value 'バックアップの保存先（フォルダかファイルのフルパス）' (Join-Path $PSScriptRoot 'signing-key-backup.json'))
    Export-SigningKeyBackup $path $p1
    Write-Ok "バックアップを作りました: $path"
    Write-Host '   このファイルは自分のPCやUSBメモリなど、VPSの外にコピーして保管してください（VPSからは消してかまいません）'
    Write-Host '   配信フォルダには絶対に置かないでください'
}

# 鍵がなければ作る（バックアップから戻すこともできる）。作ったら確認コードを見せる
function Initialize-SigningKey([string] $manifestPath) {
    if (Get-SigningKey) { return }
    Write-Step 'サーバーリストに署名するための鍵を用意します（最初の1回だけ）'
    Write-SigningExplanation
    $published = if ($manifestPath) { Get-PublishedSigningCode $manifestPath } else { $null }
    if ($published) {
        Write-Warning "このWindowsユーザー（$([Environment]::UserName)）には鍵がありませんが、いまのサーバーリストは確認コード $published の鍵で署名されています"
        Write-Host '   別のWindowsユーザーで作った鍵なら、そのユーザーでログインして実行してください。'
        Write-Host '   VPSを作り直したのなら、バックアップから戻すと同じ確認コードを使い続けられます。'
    }
    Write-Host ''
    Write-Host '     1. 新しく鍵を作る'
    Write-Host '     2. バックアップから戻す（以前作ったsigning-key-backup.jsonがある場合）'
    $choice = Read-Value '番号' '1'
    if ($choice -eq '2') {
        Restore-SigningKeyInteractive
        return
    }
    Save-SigningKey (New-KeyBytes)
    $key = Get-SigningKey
    Write-Ok '鍵を作りました'
    Write-Host "   秘密の鍵のファイル: $SigningKeyPath"
    Write-Host "   このWindowsユーザー（$([Environment]::UserName)）でしか開けないよう暗号化してあります。ほかのPCにコピーしても使えません"
    Show-SigningCode $key.Code
    if ($published) {
        Write-Warning "確認コードが $published から変わりました。友人には新しい確認コードを伝えてください（ランチャーの「編集」で入力し直してもらいます）"
    }
    Invoke-BackupPrompt
}

# バックアップの置き場所。フォルダを指定した場合や、拡張子のない名前を指定した場合は、その中のsigning-key-backup.jsonにする
# （ただし、拡張子のない名前でも同じ名前のファイルがすでにあれば、そのファイルを使う。以前の版はそのまま保存していたため）
function Resolve-BackupPath([string] $path, [switch] $Existing) {
    $full = Get-FullPath $path.Trim().Trim('"')
    if (Test-Path -LiteralPath $full -PathType Leaf) { return $full }
    if ((Test-Path -LiteralPath $full -PathType Container) -or -not [System.IO.Path]::GetExtension($full)) {
        if (-not $Existing) { New-Item -ItemType Directory -Force -Path $full | Out-Null }
        return (Join-Path $full 'signing-key-backup.json')
    }
    return $full
}

function Restore-SigningKeyInteractive {
    $path = Resolve-BackupPath (Read-Value 'バックアップのファイル（signing-key-backup.json）か、それを入れたフォルダのフルパス' (Join-Path $PSScriptRoot 'signing-key-backup.json')) -Existing
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "バックアップのファイルが見つかりません: $path" }
    while ($true) {
        $password = Read-Password 'バックアップのパスワード'
        try {
            Import-SigningKeyBackup $path $password
            break
        }
        catch {
            Write-Warning $_.Exception.Message
            if ((Read-Value 'もう一度試しますか？（Y/n）' 'Y') -notmatch '^[Yy]') { throw '鍵を戻せませんでした' }
        }
    }
    Write-Ok 'バックアップから鍵を戻しました'
    Show-SigningCode (Get-SigningKey).Code
}

# manifest.jsonを書き換えたあとに呼ぶ。鍵がなければ用意してから署名する
function Update-ManifestSignature([string] $manifestPath) {
    Initialize-SigningKey $manifestPath
    Invoke-ManifestSign $manifestPath
    Write-Ok "サーバーリストに署名しました（確認コード $((Get-SigningKey).Code)）"
}

# manifest.jsonを書いて署名する
function Write-ManifestFile([string] $path, $data) {
    Write-JsonFile $path $data
    Update-ManifestSignature $path
}
