"""Exercise the translation loader and source-language fallback contracts.

These tests protect supported language identifiers, packaged catalogue lookup, and
delegation to Qt translation contexts.
"""
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

from PySide6.QtCore import QCoreApplication, QTranslator

import i18n


class I18nContractTests(unittest.TestCase):
    """Cover translation helper contracts without installing real catalogues."""
    def test_source_language_and_supported_labels(self):
        self.assertEqual(i18n.DEFAULT_LANGUAGE, "en_GB")
        self.assertEqual(i18n.SUPPORTED_LANGUAGES["en_GB"], "English (United Kingdom)")
        self.assertEqual(i18n.SUPPORTED_LANGUAGES["pt_PT"], "Português (Portugal)")

    def test_normalize_language_accepts_supported_identifiers(self):
        self.assertEqual(i18n.normalize_language("en_GB"), "en_GB")
        self.assertEqual(i18n.normalize_language("pt_PT"), "pt_PT")

    def test_normalize_language_falls_back_to_source(self):
        for value in (None, "", "fr_FR", 123, object()):
            with self.subTest(value=value):
                self.assertEqual(i18n.normalize_language(value), "en_GB")

    def test_catalogue_path_is_explicit_for_source_and_supported_translation(self):
        self.assertIsNone(i18n.catalogue_path("en_GB"))
        self.assertEqual(
            i18n.catalogue_path("pt_PT"),
            Path(i18n.__file__).resolve().parent / "translations" / "rsm_pt_PT.qm",
        )
        self.assertIsNone(i18n.catalogue_path("unknown"))

    def test_frozen_catalogue_path_uses_pyinstaller_runtime_resource_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            runtime_root = Path(directory)
            catalogue = runtime_root / "translations" / "rsm_pt_PT.qm"
            catalogue.parent.mkdir()
            catalogue.touch()

            with patch.object(i18n.sys, "frozen", True, create=True), \
                    patch.object(i18n.sys, "_MEIPASS", str(runtime_root), create=True):
                self.assertEqual(i18n.catalogue_path("pt_PT"), catalogue)

    def test_english_does_not_install_a_translator(self):
        app = Mock(spec=QCoreApplication)

        self.assertIsNone(i18n.install_translator(app, "en_GB"))
        app.installTranslator.assert_not_called()

    def test_missing_catalogue_falls_back_without_crashing(self):
        app = Mock(spec=QCoreApplication)

        with patch.object(i18n.Path, "is_file", return_value=False):
            result = i18n.install_translator(app, "pt_PT")

        self.assertIsNone(result)
        app.installTranslator.assert_not_called()

    def test_successful_catalogue_is_loaded_and_installed(self):
        app = Mock(spec=QCoreApplication)
        translator = Mock(spec=QTranslator)
        translator.load.return_value = True
        app.installTranslator.return_value = True

        with patch.object(i18n.Path, "is_file", return_value=True), \
                patch.object(i18n, "QTranslator", return_value=translator):
            result = i18n.install_translator(app, "pt_PT")

        self.assertIs(result, translator)
        translator.load.assert_called_once_with(str(i18n.catalogue_path("pt_PT")))
        app.installTranslator.assert_called_once_with(translator)

    def test_translate_delegates_to_qt(self):
        # Translation context ownership is delegated directly to Qt.
        with patch.object(QCoreApplication, "translate", return_value="Translated") as qt_translate:
            result = i18n.translate("MapperWindow", "Source")

        self.assertEqual(result, "Translated")
        qt_translate.assert_called_once_with("MapperWindow", "Source")


if __name__ == "__main__":
    unittest.main()
