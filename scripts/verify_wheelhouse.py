#!/usr/bin/env python3
"""Verify every wheel in a directory against the official PyPI JSON API."""
from __future__ import annotations
import argparse, hashlib, json, pathlib, re, ssl, sys, urllib.request

WHEEL = re.compile(r"^(?P<name>.+?)-(?P<version>[^-]+)-[^-]+-[^-]+-[^-]+\.whl$")

def digest(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''): h.update(block)
    return h.hexdigest()

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('directory', type=pathlib.Path)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    args = parser.parse_args()
    records = []
    for path in sorted(args.directory.glob('*.whl')):
        match = WHEEL.match(path.name)
        if not match: raise RuntimeError(f'Unrecognized wheel name: {path.name}')
        name = match.group('name').replace('_','-')
        version = match.group('version')
        url = f'https://pypi.org/pypi/{name}/{version}/json'
        with urllib.request.urlopen(url, context=ssl.create_default_context(), timeout=60) as response:
            metadata = json.load(response)
        expected = None
        for item in metadata.get('urls', []):
            if item.get('filename') == path.name:
                expected = item.get('digests', {}).get('sha256')
                break
        actual = digest(path)
        if not expected or actual.lower() != expected.lower():
            raise RuntimeError(f'PyPI SHA-256 verification failed for {path.name}')
        records.append({'filename': path.name, 'project': name, 'version': version, 'sha256': actual})
        print(f'verified {path.name}')
    if not records: raise RuntimeError('No wheels were found.')
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({'source':'https://pypi.org','files':records}, indent=2)+'\n', encoding='utf-8')
    return 0

if __name__ == '__main__':
    try: raise SystemExit(main())
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
