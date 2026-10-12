"""Bounded screen framing/image adapter. Does not open any network connection."""
import base64
import json
import struct
import uuid
import zlib

from mcp.types import ImageContent, TextContent

MAX_METADATA = 4096
MAX_IMAGE = 4 * 1024 * 1024


class CaptureRateRefusal(ValueError):
    """Correlated refusal before capture: no image frame follows."""


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate-field")
        result[key] = value
    return result


def _reject_constant(_):
    raise ValueError("non-finite-number")


def _read_exact(stream, size):
    data = bytearray()
    while len(data) < size:
        chunk = stream.read(size - len(data))
        if not chunk:
            raise EOFError("screen-connection-closed")
        data.extend(chunk)
    return bytes(data)


def read_metadata(stream):
    size = struct.unpack(">I", _read_exact(stream, 4))[0]
    if size < 2 or size > MAX_METADATA:
        raise ValueError("metadata-size-limit")
    result = json.loads(
        _read_exact(stream, size).decode("utf-8"),
        object_pairs_hook=_unique_object,
        parse_constant=_reject_constant,
    )
    if not isinstance(result, dict):
        raise ValueError("metadata-object-required")
    return result


def write_metadata(stream, value):
    if not isinstance(value, dict):
        raise ValueError("metadata-object-required")
    data = json.dumps(value, separators=(",", ":"), allow_nan=False).encode("utf-8")
    if len(data) > MAX_METADATA:
        raise ValueError("metadata-size-limit")
    stream.write(struct.pack(">I", len(data)) + data)


def capture_content(stream, request_id=None, session_id=None):
    metadata = read_metadata(stream)
    if (set(metadata) == {"version", "request_id", "session_id", "error", "dispatched"}
            and type(metadata["version"]) is int and metadata["version"] == 1
            and metadata["request_id"] == request_id and metadata["session_id"] == session_id
            and metadata["error"] == "capture-rate-limit" and metadata["dispatched"] is False):
        raise CaptureRateRefusal("screen-capture-refused-before-dispatch: capture-rate-limit")
    fields = {"version", "request_id", "session_id", "frame_id", "monitor_id", "geometry_version", "width", "height", "captured_utc", "mime_type"}
    if set(metadata) != fields or type(metadata["version"]) is not int or metadata["version"] != 1 or metadata["mime_type"] != "image/png":
        raise ValueError("invalid-image-metadata")
    for field in ("request_id", "session_id", "frame_id"):
        if not isinstance(metadata[field], str):
            raise ValueError("invalid-image-metadata")
        uuid.UUID(metadata[field])
    if (request_id is not None and metadata["request_id"] != request_id) or (session_id is not None and metadata["session_id"] != session_id):
        raise ValueError("screen-response-mismatch")
    if not isinstance(metadata["monitor_id"], str) or not 1 <= len(metadata["monitor_id"]) <= 128:
        raise ValueError("invalid-image-metadata")
    for field in ("geometry_version", "width", "height"):
        if type(metadata[field]) is not int or metadata[field] < 1:
            raise ValueError("invalid-image-metadata")
    if not 2 <= metadata["width"] <= 1920 or not 2 <= metadata["height"] <= 1920:
        raise ValueError("image-dimension-limit")
    if not isinstance(metadata["captured_utc"], str) or len(metadata["captured_utc"]) > 40:
        raise ValueError("invalid-image-metadata")
    size = struct.unpack(">I", _read_exact(stream, 4))[0]
    if size < 33 or size > MAX_IMAGE:
        raise ValueError("image-size-limit")
    image = _read_exact(stream, size)
    if image[:8] != b"\x89PNG\r\n\x1a\n" or image[8:16] != b"\x00\x00\x00\rIHDR":
        raise ValueError("invalid-png")
    width, height = struct.unpack(">II", image[16:24])
    if (width, height) != (metadata["width"], metadata["height"]):
        raise ValueError("image-dimension-mismatch")
    _validate_png(image, width, height)
    return [TextContent(type="text", text=json.dumps(metadata, separators=(",", ":"))),
            ImageContent(type="image", mimeType="image/png", data=base64.b64encode(image).decode("ascii"))]


def _validate_png(image, width, height):
    # Non-interlaced 8-bit RGB/RGBA from the Windows encoder only. Bound decoded
    # data before allocation and reject corrupt/truncated or animated images.
    depth, color, compression, filtering, interlace = image[24:29]
    if depth != 8 or color not in (2, 6) or compression or filtering or interlace:
        raise ValueError("unsupported-png")
    expected = height * (1 + width * (3 if color == 2 else 4))
    decoder = zlib.decompressobj()
    decoded = 0
    position = 8
    ended = False
    while position < len(image):
        if position + 12 > len(image):
            raise ValueError("invalid-png")
        size = struct.unpack(">I", image[position:position + 4])[0]
        end = position + size + 12
        if end > len(image):
            raise ValueError("invalid-png")
        kind = image[position + 4:position + 8]
        data = image[position + 8:end - 4]
        if zlib.crc32(kind + data) != struct.unpack(">I", image[end - 4:end])[0]:
            raise ValueError("invalid-png")
        if kind == b"acTL" or (kind == b"IHDR" and position != 8):
            raise ValueError("unsupported-png")
        if kind == b"IDAT":
            try:
                decoded += len(decoder.decompress(data, expected - decoded + 1))
            except zlib.error as exc:
                raise ValueError("invalid-png") from exc
            if decoded > expected or decoder.unconsumed_tail or decoder.unused_data:
                raise ValueError("image-decoded-size-limit")
        if kind == b"IEND":
            ended = size == 0 and end == len(image)
            break
        position = end
    if not ended or not decoder.eof or decoded != expected:
        raise ValueError("invalid-png")
