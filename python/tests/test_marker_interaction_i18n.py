"""Verify marker interaction translation catalogue coverage.

These tests protect British English .ui source strings where present, MapperWindow
ownership, and placeholders such as {name}, {count}, and coordinates.
"""
import re
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CATALOGUE = ROOT / 'translations' / 'rsm_pt_PT.ts'
SOURCE_FILES = (ROOT / 'qt_map_operations.py', ROOT / 'rhino_surface_mapper_qt.py')


# MapperWindow owns marker strings and their {name}, {count}, and coordinate slots.
EXPECTED_MARKER_MESSAGES = {
    'Marker': 'Marca',
    'Enter the Rhino first and wait for its position.':
        'Primeiro entre no Rhino e aguarde a posição.',
    'Edit marker': 'Alterar marca',
    'Rig': 'Rig',
    'Enter the Rhino first.': 'Primeiro entre no Rhino.',
    'Click the map to place the rig. Escape cancels.':
        'Clique no mapa para colocar o rig. Escape cancela.',
    'Delete marker': 'Apagar marcador',
    'Delete this marker?': 'Apagar este marcador?',
    'Navigate': 'Navegar',
    'Stop navigation': 'Parar navegação',
    'Navigating to: {name}': 'A navegar para: {name}',
    'Navigation stopped.': 'Navegação parada.',
    'Navigation stopped. Returning to Pause Point ⏸.':
        'Navegação parada. A regressar ao Ponto de Pausa ⏸.',
    'NAVIGATE: {name}': 'NAVEGAR: {name}',
    '[Marker] {name}': '[Marca] {name}',
    '[Deposit] {name}': '[Depósito] {name}',
    'Deposit': 'Depósito',
    'Information': 'Informações',
    'Copy coordinates': 'Copiar coordenadas',
    'Edit': 'Alterar',
    'Edit deposit': 'Editar depósito',
    'Delete': 'Apagar',
    'Delete deposit': 'Apagar depósito',
    'Delete rig': 'Apagar rig',
    'Cancel': 'Cancelar',
    'Marked point': 'Ponto marcado',
    'Name: {name}': 'Nome: {name}',
    'Size: {size}': 'Tamanho: {size}',
    'Rigs: {count}': 'Rigs: {count}',
    'Latitude: {latitude}°': 'Latitude: {latitude}°',
    'Longitude: {longitude}°': 'Longitude: {longitude}°',
}


def mapper_window_messages():
    root = ET.parse(CATALOGUE).getroot()
    context = next(
        context for context in root.findall('context')
        if context.findtext('name') == 'MapperWindow')
    return {
        message.findtext('source'): message.findtext('translation')
        for message in context.findall('message')
    }


def test_marker_sources_use_mapper_window_context():
    source_text = '\n'.join(
        path.read_text(encoding='utf-8') for path in SOURCE_FILES)

    for source in EXPECTED_MARKER_MESSAGES:
        assert re.search(
            rf"translate\(\s*'MapperWindow',\s*'{re.escape(source)}'",
            source_text)


def test_marker_catalogue_contains_complete_expected_mapping():
    messages = mapper_window_messages()

    assert {
        source: messages[source]
        for source in EXPECTED_MARKER_MESSAGES
    } == EXPECTED_MARKER_MESSAGES
