import base64
import io
import json
import struct
import sys
import unittest
import uuid
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))
from remsup_screen_protocol import MAX_IMAGE, capture_content, read_metadata, write_metadata


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


def synthetic_png():
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 2, 2, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(b"\x00\xff\x00\x00\xff\x00\x00" * 2)) + chunk(b"IEND", b"")


class ScreenContentTests(unittest.TestCase):
    def metadata(self):
        return {"version": 1, "request_id": str(uuid.uuid4()), "session_id": str(uuid.uuid4()), "frame_id": str(uuid.uuid4()), "monitor_id": "synthetic", "geometry_version": 1, "width": 2, "height": 2, "captured_utc": "2026-10-08T00:00:00Z", "mime_type": "image/png"}

    def stream(self, metadata=None, image=None):
        stream = io.BytesIO()
        write_metadata(stream, self.metadata() if metadata is None else metadata)
        image = synthetic_png() if image is None else image
        stream.write(struct.pack(">I", len(image)) + image)
        stream.seek(0)
        return stream

    def test_real_mcp_image_content_from_synthetic_memory_frame(self):
        content = capture_content(self.stream())
        self.assertEqual(content[0].type, "text")
        self.assertEqual(json.loads(content[0].text)["monitor_id"], "synthetic")
        self.assertEqual(content[1].type, "image")
        self.assertEqual(content[1].mimeType, "image/png")
        self.assertEqual(base64.b64decode(content[1].data), synthetic_png())

    def test_duplicate_nonfinite_truncated_and_oversized_metadata(self):
        for body in (b'{"x":1,"x":2}', b'{"x":NaN}', b'[]'):
            with self.assertRaises(ValueError):
                read_metadata(io.BytesIO(struct.pack(">I", len(body)) + body))
        with self.assertRaises(ValueError):
            read_metadata(io.BytesIO(struct.pack(">I", 4097)))
        with self.assertRaises(EOFError):
            read_metadata(io.BytesIO(struct.pack(">I", 2) + b'{'))

    def test_invalid_dimensions_types_extra_fields_and_png(self):
        for field, value in (("width", 1921), ("height", True), ("version", True), ("frame_id", "bad"), ("mime_type", "image/jpeg"), ("extra", "private")):
            metadata = self.metadata(); metadata[field] = value
            with self.assertRaises(ValueError):
                capture_content(self.stream(metadata))
        with self.assertRaises(ValueError):
            capture_content(self.stream(image=b'x' * 33))
        metadata = self.metadata(); metadata["width"] = 3
        with self.assertRaises(ValueError):
            capture_content(self.stream(metadata))

    def test_payload_cap_before_read_and_disconnect(self):
        stream = io.BytesIO(); write_metadata(stream, self.metadata())
        stream.write(struct.pack(">I", MAX_IMAGE + 1)); stream.seek(0)
        with self.assertRaises(ValueError):
            capture_content(stream)
        stream = self.stream(); truncated = stream.getvalue()[:-1]
        with self.assertRaises(EOFError):
            capture_content(io.BytesIO(truncated))

    def test_png_crc_end_and_decoded_budget(self):
        image = synthetic_png()
        for bad in (image[:-12], image[:-1] + b'x', image + b'x'):
            with self.assertRaises(ValueError):
                capture_content(self.stream(image=bad))
        bomb = image[:33] + chunk(b"IDAT", zlib.compress(b'x' * 10000)) + chunk(b"IEND", b"")
        with self.assertRaises(ValueError):
            capture_content(self.stream(image=bomb))


if __name__ == "__main__":
    unittest.main()
