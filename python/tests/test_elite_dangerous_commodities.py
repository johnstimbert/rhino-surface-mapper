"""Tests for the surface commodity catalogue and commodity name normalization."""

import json
from pathlib import Path
import tempfile
import unittest

from elite_dangerous.market.commodities import (
    CATALOGUE_PATH,
    CatalogueFormatError,
    COMMODITY_ALIASES,
    SURFACE_COMMODITIES,
    _load_catalogue,
    canonical_commodity_name,
    normalize_name,
)


class CommodityTests(unittest.TestCase):
    """Covers catalogue validation, immutability, aliases, and normalization."""

    def setUp(self):
        self.payload = json.loads(CATALOGUE_PATH.read_text(encoding="utf-8"))

    def write_catalogue(self, payload):
        """Create an isolated catalogue file for validation scenarios."""
        temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(temporary_directory.cleanup)
        path = Path(temporary_directory.name) / "catalogue.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        return path

    def test_current_adopted_catalogue_has_37_products(self):
        # The Surface Mining workflow intentionally uses the adopted 37-product list.
        self.assertEqual(len(SURFACE_COMMODITIES), 37)
        self.assertEqual(len(set(SURFACE_COMMODITIES)), len(SURFACE_COMMODITIES))

    def test_json_catalogue_loads_and_derives_public_tuple(self):
        catalogue = _load_catalogue(CATALOGUE_PATH)
        self.assertEqual(
            tuple(record["name"] for record in catalogue), SURFACE_COMMODITIES
        )
        self.assertEqual(len(catalogue), 37)
        self.assertIsInstance(SURFACE_COMMODITIES, tuple)

    def test_catalogue_uses_only_allowed_nonempty_planet_types(self):
        allowed = {"High metal content", "Metal Rich", "Rocky", "Rocky Ice", "Icy"}
        for record in self.payload:
            self.assertTrue(record["planet_types"])
            self.assertTrue(set(record["planet_types"]) <= allowed)

    def test_catalogue_rejects_duplicate_commodity_names(self):
        payload = list(self.payload)
        payload[1] = dict(payload[1], name=payload[0]["name"])
        with self.assertRaises(CatalogueFormatError):
            _load_catalogue(self.write_catalogue(payload))

    def test_catalogue_rejects_duplicate_planet_types(self):
        payload = list(self.payload)
        first = payload[0]
        payload[0] = dict(
            first,
            planet_types=first["planet_types"] + [first["planet_types"][0]],
        )
        with self.assertRaises(CatalogueFormatError):
            _load_catalogue(self.write_catalogue(payload))

    def test_catalogue_rejects_invalid_structure_and_missing_file(self):
        invalid_payloads = (
            {},
            self.payload[:-1],
            [dict(self.payload[0], unexpected=True)] + self.payload[1:],
            [dict(self.payload[0], name="")] + self.payload[1:],
            [dict(self.payload[0], planet_types=[])] + self.payload[1:],
            [dict(self.payload[0], planet_types=["Unknown"])] + self.payload[1:],
        )
        for payload in invalid_payloads:
            with self.subTest(payload=payload):
                with self.assertRaises(CatalogueFormatError):
                    _load_catalogue(self.write_catalogue(payload))

        with self.assertRaises(CatalogueFormatError):
            _load_catalogue(Path("missing-surface-catalogue.json"))

    def test_catalogue_rejects_invalid_json_and_encoding(self):
        temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(temporary_directory.cleanup)
        malformed_path = Path(temporary_directory.name) / "malformed.json"
        malformed_path.write_text("{", encoding="utf-8")
        with self.assertRaises(CatalogueFormatError):
            _load_catalogue(malformed_path)

        encoding_path = Path(temporary_directory.name) / "encoding.json"
        encoding_path.write_bytes(b"\xff")
        with self.assertRaises(CatalogueFormatError):
            _load_catalogue(encoding_path)

    def test_catalogue_is_immutable_and_consumers_need_not_assume_order(self):
        self.assertIsInstance(SURFACE_COMMODITIES, tuple)
        self.assertEqual(
            set(SURFACE_COMMODITIES),
            set(reversed(SURFACE_COMMODITIES)),
        )
        with self.assertRaises(TypeError):
            COMMODITY_ALIASES["Another name"] = "Helium"

    def test_normalization_ignores_case_accents_punctuation_and_spaces(self):
        self.assertEqual(normalize_name("Bastnäsite"), "bastnasite")
        self.assertEqual(normalize_name("  LOW-temperature  DIAMONDS! "), "lowtemperaturediamonds")
        self.assertEqual(normalize_name("Helium-3"), normalize_name("helium 3"))

    def test_canonical_names_resolve_to_adopted_spelling(self):
        self.assertEqual(canonical_commodity_name("bastnäsite"), "Bastnäsite")
        self.assertEqual(canonical_commodity_name("LOW temperature diamonds"), "Low Temperature Diamonds")

    def test_demonstrated_aliases_resolve_to_canonical_names(self):
        self.assertEqual(canonical_commodity_name("Bastnasite"), "Bastnäsite")
        self.assertEqual(
            canonical_commodity_name("Methanol Monohydrate Crystals"),
            "Methanol Crystals",
        )

    def test_unknown_product_returns_none(self):
        self.assertIsNone(canonical_commodity_name("Unobtainium"))
        self.assertIsNone(canonical_commodity_name(None))


if __name__ == "__main__":
    unittest.main()
