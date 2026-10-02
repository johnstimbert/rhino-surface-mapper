"""Verify MapperWindow map-lifecycle translation catalogue coverage.

These tests protect British English .ui source strings where present, class-owned
contexts, and placeholders such as {pml_id} and {error}.
"""
import re
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CATALOGUE = ROOT / 'translations' / 'rsm_pt_PT.ts'
SOURCE_FILES = (ROOT / 'qt_map_operations.py', ROOT / 'rhino_surface_mapper_qt.py')


def mapper_window_messages():
    root = ET.parse(CATALOGUE).getroot()
    context = next(
        context for context in root.findall('context')
        if context.findtext('name') == 'MapperWindow')
    return {
        message.findtext('source'): message.findtext('translation')
        for message in context.findall('message')
    }


def source_mapper_window_messages():
    pattern = re.compile(r"translate\(\s*'MapperWindow',\s*'([^']*)'")
    return {
        source
        for path in SOURCE_FILES
        for source in pattern.findall(path.read_text(encoding='utf-8'))
    }


def test_lifecycle_sources_are_catalogued_in_mapper_window_context():
    messages = mapper_window_messages()
    sources = source_mapper_window_messages()

    assert sources <= messages.keys()
    assert all(messages[source] for source in sources)


def test_lifecycle_catalogue_preserves_dynamic_values_and_core_translations():
    messages = mapper_window_messages()

    # Dynamic placeholders such as {pml_id} and {error} must survive translation.
    expected = {
        'PML centre [{pml_id}]': 'Centro do PML [{pml_id}]',
        'Bearing from Rhino to the PML centre (0° = north):':
            'Azimute do Rhino para o centro do PML (0° = norte):',
        'PML [{pml_id}] created. Save the map to keep it.':
            'PML [{pml_id}] criado. Guarde o mapa para o conservar.',
        'Open PML [{pml_id}]': 'Abrir PML [{pml_id}]',
        '[{pml_id}] — saved {timestamp} — {distance:.1f} km':
            '[{pml_id}] — guardado {timestamp} — {distance:.1f} km',
        'Map opened: {filename}': 'Mapa aberto: {filename}',
        'Map saved: {filename}': 'Mapa guardado: {filename}',
        'The current map has no associated file yet.':
            'O mapa atual ainda não tem um ficheiro associado.',
        'Error saving': 'Erro ao guardar',
    }

    assert {source: messages[source] for source in expected} == expected


def test_lifecycle_catalogue_contains_a_mapper_window_entry_for_each_source():
    root = ET.parse(CATALOGUE).getroot()
    mapper_sources = {
        message.findtext('source')
        for context in root.findall('context')
        for message in context.findall('message')
        if context.findtext('name') == 'MapperWindow'
    }

    assert source_mapper_window_messages() <= mapper_sources
