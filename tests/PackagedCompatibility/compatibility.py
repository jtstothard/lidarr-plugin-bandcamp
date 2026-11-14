"""Shared packaged-compatibility inputs; callers retain verification and controls."""
from pathlib import Path
import tarfile


SCENARIOS = ('healthy', 'warning', 'restored', 'disabled', 'multiple')


def extract_runtime_dlls(archive_path, runtime):
    # Extract flat DLLs only. No archive paths, symlinks, services or native executables.
    with tarfile.open(archive_path) as archive:
        for member in archive:
            if member.isfile() and member.name.startswith('Lidarr/') and member.name.count('/') == 1 and member.name.endswith('.dll'):
                (runtime / Path(member.name).name).write_bytes(archive.extractfile(member).read())
