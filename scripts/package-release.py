#!/usr/bin/env python3
"""生成 Microsoft Rewards Script 便携发布包（Python 版本，绕开 PowerShell 回收站拦截器）"""
import json
import os
import re
import shutil
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXCLUDE_RE = re.compile(
    r'(?:\\|/)(?:__tests__|examples|fixtures|node_modules)(?:\\|/)|(?:test|spec)\.ts$|\.d\.ts$|\.js\.map$'
)

with open(os.path.join(REPO, 'package.json'), encoding='utf-8') as f:
    version = json.load(f)['version']

staging = os.path.join(os.environ['TEMP'], f'mrs-release-staging-py-{version}')
output_dir = os.path.join(REPO, 'release')
os.makedirs(output_dir, exist_ok=True)
zip_path = os.path.join(output_dir, f'Microsoft-Rewards-Script-portable-v{version}.zip')

# 清理旧暂存/旧 zip
if os.path.exists(staging):
    shutil.rmtree(staging, ignore_errors=True)
os.makedirs(staging, exist_ok=True)
if os.path.exists(zip_path):
    os.remove(zip_path)

files = [
    'package.json',
    'package-lock.json',
    'config.example.json',
    '.env.example',
    'env.example',
    'tsconfig.json',
    'src/',
    'scripts/main/copyAssets.js',
    'autorun/RewardsManager.exe',
    'autorun/run-rewards.ps1',
    'autorun/setup-task.ps1',
    'autorun/diagnose.ps1',
    'autorun/diagnose.bat',
    'autorun/run-manual.bat',
    'autorun/setup-task.bat',
]

include_docs = '--include-docs' in [a.lower() for a in os.sys.argv[1:]]
if include_docs:
    files.extend(['README.md', 'LICENSE'])

print(f'version={version}')
print(f'staging -> {staging}')

for rel in files:
    src = os.path.join(REPO, rel)
    if not os.path.exists(src):
        print(f'WARN skip missing: {rel}')
        continue
    if rel.endswith('/'):
        rel = rel[:-1]
    dst = os.path.join(staging, rel)
    if os.path.isdir(src):
        for root, dirs, files_here in os.walk(src):
            for fname in files_here:
                src_file = os.path.join(root, fname)
                rel_path = os.path.relpath(src_file, REPO)
                if EXCLUDE_RE.search(src_file):
                    continue
                dst_file = os.path.join(staging, rel_path)
                os.makedirs(os.path.dirname(dst_file), exist_ok=True)
                shutil.copy2(src_file, dst_file)
        print(f'+ {rel}/')
    else:
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        print(f'+ {rel}')

# 打包
with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as zf:
    for root, dirs, files_here in os.walk(staging):
        for fname in files_here:
            full = os.path.join(root, fname)
            arcname = os.path.relpath(full, staging)
            zf.write(full, arcname)

size_mb = os.path.getsize(zip_path) / (1024 * 1024)
print(f'OK {zip_path}')
print(f'  文件数: {len(zf.namelist())}')
print(f'  大小: {size_mb:.2f} MB')
