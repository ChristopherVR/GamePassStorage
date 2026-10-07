# Test fixtures

`GamePassSaves/0009000000000001_0000000000000000000000000000ABCD` is one small, sanitized Xbox
Connected Storage ("wgs") store holding a single Abiotic Factor world bundle (`TestWorld-WC`, about
24 KB). Its ids are synthetic (`Synthetic.AbioticTest_0000000000000!App`, ETag-free, all-zero account)
and it holds no personal data. It was produced for the Abiotic Editor test suite and copied here
because it is the only real-layout world bundle small enough to keep in the repository.

`SyntheticMultiBlob` is a deterministic, synthetic store with three named blobs (`Meta`, `Body`,
`Thumb`) and an uninterpreted four-byte manifest tail. It is generated independently of the .NET
library by `python tests/fixtures/generate_synthetic.py`, using the wire layout in
`docs/wgs-format.md`. All identifiers and payloads are fabricated; there is no account or player
data. Tests exercise fixture parsing, export/import, preserving untouched blobs and manifest tails,
and restoring with colliding ids or interrupted writes. This is regression coverage, not evidence
of support for a real game's multi-blob saves; `RealMultiBlob` is that evidence.

`RealMultiBlob/0009000000000002_00000000000000000000000000000001` is a real store written by the game
for package `BethesdaSoftworks.ProjectTitan_3275kfvn8vcwc`, captured on 2026-10-08 with
`wgs sanitize`: `containers.index` and both `container.N` manifests are byte for byte as the game
wrote them, except the index's root GUID, which is zeroed; every blob is zero-filled at its real size.
The folder name replaces the real account id. It keeps the real package family name, container and
blob names and ETags, none of which identify a person. `GAME-AUTOSAVE1` holds six blobs, the first
real multi-blob container in this repository; `RealMultiBlobFixtureTests` pins its layout.

Policy for new fixtures:

- Keep each fixture under 2 MB and sanitize it (`wgs sanitize` does the store part): synthetic account
  ids, no player names, no paths from a real machine. A real package family name may stay when the
  fixture is evidence for that title. Say in this file where it came from.
- Prefer building synthetic payloads inside the test (see the bundle builder in
  `AbioticAdapterTests`) when a real one is not needed to prove a layout.
- A fixture proves a layout for the title it came from. It is not evidence that other titles work.
