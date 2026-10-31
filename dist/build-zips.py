"""
配布用のzipを2つ作る（simutrans_Launcher.zip と simutrans_ServerTools.zip）。

使い方（リポジトリの一番上で実行する）:
    python3 -I dist/build-zips.py <exeのあるフォルダ> <zipを書き出すフォルダ>

exeのあるフォルダには、dotnet publish で作った Simutrans_Launcher.exe と Simutrans_ServerManager.exe を置いておく。
リポジトリの中身は git ls-files で取るので、公開の設定や鍵のバックアップなど、手元にしかないファイルは入らない。
日本語のファイル名が文字化けしないよう、zipにUTF-8のフラグ（0x800）を立てる。
アップロードの上限（30MiB）に収めるため、ランチャーとサーバー側の道具でzipを分けている。
"""
import hashlib
import os
import subprocess
import sys
import time
import zipfile

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
exe_dir, out_dir = sys.argv[1], sys.argv[2]
tracked = subprocess.run(['git', '-C', repo, 'ls-files'], capture_output=True, text=True, check=True).stdout.splitlines()
stamp = time.localtime()[:6]


def write(name, top, entries):
    path = os.path.join(out_dir, name)
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        for arc, src in entries:
            info = zipfile.ZipInfo(f'{top}/{arc}', stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.flag_bits |= 0x800
            with open(src, 'rb') as f:
                z.writestr(info, f.read())
    with open(path, 'rb') as f:
        print(f'{name}  {os.path.getsize(path):,} bytes  sha256 {hashlib.sha256(f.read()).hexdigest()}')


def repo_files(prefix):
    return [(p, os.path.join(repo, p)) for p in tracked if p.startswith(prefix)]


os.makedirs(out_dir, exist_ok=True)
write('simutrans_Launcher.zip', 'simutrans_Launcher', [
    ('はじめにお読みください.md', os.path.join(repo, 'dist', 'readme-launcher.md')),
    ('README.md', os.path.join(repo, 'README.md')),
    ('launcher/Simutrans_Launcher.exe', os.path.join(exe_dir, 'Simutrans_Launcher.exe')),
    *[(p, os.path.join(repo, p)) for p in ['docs/quickstart-client.md', 'docs/faq-client.md', 'docs/security.md',
                                            'docs/design-spec.md', 'docs/screenshot.png']],
    *repo_files('manifest/'),
])
write('simutrans_ServerTools.zip', 'simutrans_ServerTools', [
    ('はじめにお読みください.md', os.path.join(repo, 'dist', 'readme-servertools.md')),
    *[(p, os.path.join(repo, p)) for p in ['docs/quickstart-server.md', 'docs/faq-server.md', 'docs/security.md']],
    *repo_files('manifest/'),
    *repo_files('server-setup/'),
    ('server-setup/Simutrans_ServerManager.exe', os.path.join(exe_dir, 'Simutrans_ServerManager.exe')),
])
