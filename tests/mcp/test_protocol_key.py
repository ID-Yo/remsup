import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))

from remsup_relay_client import PROTOCOL_KEY_MASK, make_protocol_key, normalize_device_id


class ProtocolKeyTests(unittest.TestCase):
    def test_protocol_key_matches_mask(self):
        key = make_protocol_key()
        self.assertEqual(len(key), 30)
        for expected, actual in zip(PROTOCOL_KEY_MASK, key):
            if expected == "D":
                self.assertTrue(actual.isdigit())
            else:
                self.assertTrue(actual.isalpha())

    def test_normalize_device_id_keeps_digits_only(self):
        self.assertEqual(normalize_device_id("345 345 345"), "345345345")


if __name__ == "__main__":
    unittest.main()
