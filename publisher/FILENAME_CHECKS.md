# Publisher filename preflight

Every single-folder Push and full Publish checks all listed source files before the change scan, including no-op uploads and runs with deep checks disabled. It repairs case-only file and parent-directory names, keeping existing published Git spelling stable. New files use their validated manifest spelling. FileDataIDs and asset bytes stay unchanged; source manifest path spelling is synchronized when needed.

The preflight checks for missing files, duplicate IDs/paths, inconsistent directory spellings, traversal and junctions before repairs. Only unambiguous case differences are repaired. Missing or unsafe files block publication. A fresh exact-name inventory and content hashes verify repairs before staging. Scans and Check report required repairs without changing files.

Backups and move journals live under the configured E-drive `BACKUPS/filename-case-*` directory. Each repair stores the original patch.json, before/after paths, hashes, planned/completed move records and a final status. A rename failure restores prior names and exact manifest bytes. No extra asset copies or FileDataID allocation are needed for a casing change.

The committed Git snapshot remains the final authority: resumed uploads validate exact manifest/index/Git paths, directory spelling, IDs, sizes and blob hashes before any push. No updater executable rebuild or game patch reupload is needed to install this publisher change. The existing Publisher EXE launches these Python files; reopen an older running Publisher to load changes.
