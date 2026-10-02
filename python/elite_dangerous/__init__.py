"""Qt-free Elite Dangerous telemetry and market domain layer.

This package contains pure Python readers for Elite Dangerous local telemetry files
and a self-contained market subpackage for commodity acquisition, filtering,
ranking, parsing, and caching. It deliberately has no Qt dependencies so UI code
can consume stable value objects and services without coupling domain rules to
widgets or event handling.

Important invariants are that local readers report only observed game state,
market objects remain provider-independent, and persistence/network failures are
surfaced as explicit exceptions or recoverable market issues by the responsible
module.
"""
