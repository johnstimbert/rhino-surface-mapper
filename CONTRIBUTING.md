# Contributing

Thank you for helping improve Rhino Surface Mapper.

## Issues and pull requests

Please use [GitHub Issues](https://github.com/pbgaspar/rhino-surface-mapper/issues)
for reproducible bugs, feature ideas, and questions. Pull requests should
explain the user-visible change and include focused tests when behaviour is
changed.

## Repository layout

The original Python/PySide6 application lives in [`python/`](python). The .NET
port lives in `src/` and `tests/` at the repository root.

## Development (Python application)

Use the project virtual environment and install the development requirements
from the `python/` directory:

```powershell
cd python
.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
.venv\Scripts\python.exe -m pytest tests -ra
```

Source code, comments, documentation, and new technical identifiers should be
written in English. Translation contributions should preserve the Qt Linguist
workflow and include the relevant Portuguese catalogue updates.

The project is distributed under the GNU GPL-3.0; see [LICENSE](LICENSE).
