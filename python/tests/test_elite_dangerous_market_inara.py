"""Tests for parsing INARA commodity summary tables into market models."""

import unittest

from elite_dangerous.market import (
    InaraParseError,
    parse_inara_summaries,
)


def commodity_row(name, average, maximum, middle="ignored"):
    """Build a representative INARA table row with prices in columns 1 and 4."""
    # Columns 0, 1, and 4 are commodity name, average sell, and maximum sell.
    return (
        f"<tr><td>{name}</td><td>{average}</td><td>{middle}</td>"
        f"<td>also ignored</td><td>{maximum}</td><td>last</td></tr>"
    )


def document(*rows):
    """Wrap row snippets in surrounding HTML that the parser must ignore."""
    return (
        "<html><body><p>surrounding page text</p><table>"
        + "".join(rows)
        + "</table></body></html>"
    )


class InaraSummaryParsingTests(unittest.TestCase):
    """Covers INARA table detection, aliases, prices, and recoverable issues."""

    def test_maps_mature_table_columns_and_ignores_surrounding_html(self):
        html = document(commodity_row("Platinum", "134,148 Cr", "0 Cr"))
        result = parse_inara_summaries(html, ("Platinum",))

        self.assertEqual(len(result.summaries), 1)
        summary = result.summaries[0]
        self.assertEqual(summary.commodity, "Platinum")
        self.assertEqual(summary.average_sell, 134148)
        self.assertEqual(summary.maximum_sell, 0)
        self.assertIsNone(summary.total_demand)
        self.assertTrue(result.is_complete)

    def test_matches_normalized_names_and_both_demonstrated_aliases(self):
        html = document(
            commodity_row("LOW-temperature diamonds", "1,000", "2,000"),
            commodity_row("Bastnasite", "3,000", "4,000"),
            commodity_row("Methanol Monohydrate Crystals", "5,000", "6,000"),
        )
        requested = (
            "Low Temperature Diamonds",
            "Bastnäsite",
            "Methanol Crystals",
        )
        result = parse_inara_summaries(html, requested)

        self.assertEqual(result.missing, ())
        self.assertEqual(
            tuple(item.commodity for item in result.summaries),
            requested,
        )
        self.assertTrue(result.is_complete)

    def test_wrong_commodity_and_empty_normalization_keys_do_not_match(self):
        html = document(
            commodity_row("Gold", "1,000", "2,000"),
            commodity_row("!!!", "3,000", "4,000"),
        )
        result = parse_inara_summaries(html, ("Platinum", "???"))

        self.assertEqual(result.summaries, ())
        self.assertEqual(result.missing, ("Platinum", "???"))
        self.assertFalse(result.is_complete)

    def test_unknown_requested_product_is_supported_without_catalogue_limit(self):
        html = document(commodity_row("New-Community Product", "75", "90"))
        result = parse_inara_summaries(
            html,
            ("New Community Product",),
        )

        self.assertEqual(result.missing, ())
        self.assertEqual(result.summaries[0].average_sell, 75)
        self.assertTrue(result.is_complete)

    def test_partial_numeric_values_are_preserved_and_issue_is_reported(self):
        html = document(commodity_row("Platinum", "0 Cr", "not available"))
        result = parse_inara_summaries(html, ("Platinum",))

        self.assertEqual(result.summaries[0].average_sell, 0)
        self.assertIsNone(result.summaries[0].maximum_sell)
        self.assertIsNone(result.summaries[0].total_demand)
        self.assertEqual(len(result.issues), 1)
        self.assertFalse(result.is_complete)

    def test_missing_and_malformed_numeric_values_do_not_become_zero(self):
        html = document(
            commodity_row("Platinum", "—", "unknown"),
            commodity_row("Gold", "1,234", ""),
        )
        result = parse_inara_summaries(html, ("Platinum", "Gold"))

        self.assertEqual(len(result.summaries), 1)
        self.assertEqual(result.summaries[0].commodity, "Gold")
        self.assertEqual(result.summaries[0].average_sell, 1234)
        self.assertIsNone(result.summaries[0].maximum_sell)
        self.assertEqual(len(result.issues), 2)

    def test_unrelated_numeric_prose_is_not_parsed_as_a_price(self):
        html = document(
            commodity_row("Platinum", "No data; 12 entries", "Not listed: 8 offers"),
            commodity_row("Gold", "1,234 Cr", "2,345 Cr"),
        )
        result = parse_inara_summaries(html, ("Platinum", "Gold"))

        self.assertEqual(tuple(item.commodity for item in result.summaries), ("Gold",))
        self.assertEqual(result.summaries[0].average_sell, 1234)
        self.assertEqual(result.summaries[0].maximum_sell, 2345)
        self.assertEqual(result.missing, ("Platinum",))
        self.assertEqual(len(result.issues), 1)

    def test_absent_requested_item_is_derived_missing_not_fatal(self):
        result = parse_inara_summaries(
            document(
                commodity_row("Platinum", "1", "2"),
                commodity_row("Gold", "3", "4"),
            ),
            ("Copper",),
        )

        self.assertEqual(result.missing, ("Copper",))
        self.assertFalse(result.is_complete)
        self.assertEqual(result.summaries, ())

    def test_unrelated_five_column_table_is_not_a_supported_inara_table(self):
        html = (
            "<table><tr><th>Item</th><th>One</th><th>Two</th>"
            "<th>Three</th><th>Four</th></tr>"
            "<tr><td>Alpha</td><td>1</td><td>2</td><td>3</td><td>4</td></tr>"
            "</table>"
        )

        with self.assertRaises(InaraParseError):
            parse_inara_summaries(html, ("Platinum",))

    def test_valid_rows_survive_a_malformed_relevant_row(self):
        malformed = "<tr><td>Gold</td><td>bad</td><td>x</td><td>y</td><td>bad</td></tr>"
        html = document(
            commodity_row("Platinum", "100", "200"),
            malformed,
        )
        result = parse_inara_summaries(html, ("Platinum", "Gold"))

        self.assertEqual(tuple(s.commodity for s in result.summaries), ("Platinum",))
        self.assertEqual(result.missing, ("Gold",))
        self.assertEqual(len(result.issues), 1)
        self.assertFalse(result.is_complete)

    def test_last_successful_duplicate_row_wins_as_in_mature_experiments(self):
        html = document(
            commodity_row("Platinum", "100", "200"),
            commodity_row("Platinum", "300", "400"),
        )
        result = parse_inara_summaries(html, ("Platinum",))

        self.assertEqual(len(result.summaries), 1)
        self.assertEqual(result.summaries[0].average_sell, 300)
        self.assertEqual(result.summaries[0].maximum_sell, 400)

    def test_document_without_supported_table_structure_is_fatal(self):
        for html in (
            "",
            "<html><p>commodity detail page</p></html>",
            "<table><tr><td>Platinum</td><td>100</td></tr></table>",
        ):
            with self.subTest(html=html):
                with self.assertRaises(InaraParseError):
                    parse_inara_summaries(html, ("Platinum",))


if __name__ == "__main__":
    unittest.main()
