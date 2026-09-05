#!/usr/bin/env python3
"""Fetch the complete A symbol block from NYSE's public 2026-04-01 TAQ sample.

Downloads bounded gzip prefixes because the source files are sorted by symbol.
Requires a following symbol to prove that the extracted A block is complete.
"""
import argparse
import csv
import hashlib
import io
import json
from pathlib import Path
import urllib.request
import zlib

BASE_URL = 'https://ftp.nyse.com/Historical%20Data%20Samples/DAILY%20TAQ/'
DATE = '20260401'
SOURCES = {'nbbo': ('NBBO', 8 * 1024 * 1024), 'trade': ('TRADE', 4 * 1024 * 1024)}


def extract_symbol(prefix):
    decoded = zlib.decompressobj(31).decompress(prefix)
    # Discard the partial last line from this deliberately bounded gzip prefix.
    lines = decoded.rsplit(b'\n', 1)[0].splitlines()
    header = lines[0].decode('ascii').split('|')
    symbol_index = header.index('Symbol')
    selected = []
    next_symbol = None
    for line in lines[1:]:
        fields = line.decode('ascii').split('|')
        if len(fields) != len(header):
            raise ValueError('Malformed source row')
        if fields[symbol_index] == 'A':
            selected.append(line)
        elif selected:
            next_symbol = fields[symbol_index]
            break
    if not selected or next_symbol is None:
        raise ValueError('Prefix does not contain a proven complete A symbol block')
    return b'\n'.join([lines[0], *selected]) + b'\n', next_symbol


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path('.local-data/nyse-taq') / DATE)
    parser.add_argument('--prefix-directory', type=Path,
                        help='Reuse previously downloaded nbbo-prefix.gz and trade-prefix.gz')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = {'date': DATE, 'symbol': 'A', 'files': {}}
    for kind, (source_kind, prefix_size) in SOURCES.items():
        url = BASE_URL + f'EQY_US_ALL_{source_kind}_{DATE}.gz'
        if args.prefix_directory:
            prefix = (args.prefix_directory / f'{kind}-prefix.gz').read_bytes()
        else:
            request = urllib.request.Request(url, headers={'Range': f'bytes=0-{prefix_size - 1}'})
            with urllib.request.urlopen(request, timeout=45) as response:
                if response.status != 206:
                    raise ValueError('Server did not honor the bounded range request')
                prefix = response.read(prefix_size + 1)
            if len(prefix) != prefix_size:
                raise ValueError('Unexpected compressed prefix length')
        data, next_symbol = extract_symbol(prefix)
        name = f'A-{kind}-{DATE}.psv'
        rows = list(csv.DictReader(io.StringIO(data.decode('ascii')), delimiter='|'))
        sequence_field = 'Sequence_Number' if kind == 'nbbo' else 'Sequence Number'
        keys = [(row['Time'], int(row[sequence_field])) for row in rows]
        if keys != sorted(keys) or len(keys) != len(set(keys)):
            raise ValueError('Source contains ordering regressions or duplicate event keys')
        (args.output / name).write_bytes(data)
        manifest['files'][name] = {
            'source': url, 'rows': len(rows), 'first_sip_time': rows[0]['Time'],
            'last_sip_time': rows[-1]['Time'], 'next_source_symbol': next_symbol,
            'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data),
        }
    (args.output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
