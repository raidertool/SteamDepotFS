# Changelog

## v0.2.0

Changes since v0.1.1:

- ci: track changelog in release archives (e3aead8)
- feat: add Windows WinFsp mount support (5e08303)
- feat: add native macOS macFUSE mount support (4f7f134)
- ci: bound macOS mount smoke teardown (33996d1)
- fix: improve macFUSE FSKit mount validation (168ce81)
- fix: let macFUSE FSKit create volume mountpoints (2b81de6)
- ci: split hosted and self-hosted macOS mount checks (db6edc8)
- refactor: clean up cross-platform mount code (04d5941)
- Merge pull request #2 from raidertool/codex/cross-platform-mount-support (a8048c9)

## v0.1.1

Changes since v0.1.0:

- docs: add Apache license and notices (3a67b23)
- ci: generate changelog on release (1a1c84e)
- perf: add bounded chunk read-ahead (38c8352)

## v0.1.0

Initial release.

- feat: add Steam depot reader and FUSE mount (f448497)
- docs: add usage guide (c840405)
- ci: add public depot validation (4807be0)
- fix: release opened FUSE directories (9548e70)
- docs: polish public readme (20d786e)
- docs: clarify cache watermarks (cf66a64)
- ci: add authenticated depot smoke test (c477418)
- ci: let auth smoke choose manifest file (3cbba83)
- ci: release from main conventional commits (2edb644)
