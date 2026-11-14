#!/usr/bin/env python3
"""Release gate: full behavior matrix and supplementary ABI scan of distribution bytes."""
import argparse
import hashlib
import subprocess
import sys
from pathlib import Path

from run import HERE, verify


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', type=Path)
    parser.add_argument('--receipt', type=Path, required=True)
    args = parser.parse_args()
    original = hashlib.sha256(args.package.read_bytes()).hexdigest()
    subprocess.run([sys.executable, str(HERE / 'run_matrix.py'), str(args.package),
                    '--receipt', str(args.receipt), '--scan'], check=True)
    verify(args.package, original)


if __name__ == '__main__':
    main()
