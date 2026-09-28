# Test fixtures

`GamePassSaves/0009000000000001_0000000000000000000000000000ABCD` is one small, sanitized Xbox
Connected Storage ("wgs") store holding a single Abiotic Factor world bundle (`TestWorld-WC`, about
24 KB). Its ids are synthetic (`Synthetic.AbioticTest_0000000000000!App`, ETag-free, all-zero account)
and it holds no personal data. It was produced for the Abiotic Editor test suite and copied here
because it is the only real-layout world bundle small enough to keep in the repository.

Policy for new fixtures:

- Keep each fixture under 2 MB and sanitize it: synthetic account, package and world ids, no player
  names, no paths from a real machine. Say in this file where it came from.
- Prefer building synthetic payloads inside the test (see the bundle builder in
  `AbioticAdapterTests`) when a real one is not needed to prove a layout.
- A fixture proves a layout for the title it came from. It is not evidence that other titles work.
