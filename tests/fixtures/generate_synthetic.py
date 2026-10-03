"""Generate deterministic synthetic WGS fixtures without using the library under test.

Wire layout: docs/wgs-format.md. No real game or player data is used.
Run with Python 3 from any directory; only writes the named SyntheticMultiBlob fixture.
"""
from pathlib import Path
import struct
import uuid

ROOT = Path(__file__).resolve().parent / "SyntheticMultiBlob"
FOLDER = uuid.UUID("11111111-2222-3333-4444-555555555555")
BLOBS = [
    ("Meta", uuid.UUID("aaaaaaaa-0000-0000-0000-000000000001"), b'{"slot":"Synthetic","version":1}\n'),
    ("Body", uuid.UUID("aaaaaaaa-0000-0000-0000-000000000002"), bytes(range(256))),
    ("Thumb", uuid.UUID("aaaaaaaa-0000-0000-0000-000000000003"), b"synthetic thumbnail\x00\xff"),
]
FILETIME = 133801632000000000

def wstring(value):
    encoded = value.encode("utf-16-le")
    return struct.pack("<I", len(encoded) // 2) + encoded

folder = ROOT / FOLDER.hex.upper()
folder.mkdir(parents=True, exist_ok=True)
manifest = struct.pack("<II", 4, len(BLOBS))
for name, blob_id, data in BLOBS:
    manifest += name.encode("utf-16-le").ljust(128, b"\0") + blob_id.bytes_le * 2
    (folder / blob_id.hex.upper()).write_bytes(data)
# Exercise preservation of uninterpreted manifest tail bytes as well as multiple blobs.
(folder / "container.1").write_bytes(manifest + b"\xDE\xAD\xBE\xEF")
index = struct.pack("<III", 14, 1, 0)
index += wstring("Synthetic.MultiBlob_0000000000000!App")
index += struct.pack("<qI", FILETIME, 0)
index += wstring("00000000-0000-0000-0000-000000000000") + bytes([0, 0, 0, 16, 0, 0, 0, 0])
index += wstring("SyntheticSlot") * 2 + wstring("")
index += struct.pack("<BI", 1, 5) + FOLDER.bytes_le
index += struct.pack("<qqq", FILETIME, 0, sum(len(data) for _, _, data in BLOBS))
(ROOT / "containers.index").write_bytes(index)
