# Matching Virgil publisher (1.5.0)

These scripts mirror the maintainer's E-drive publisher. Keep virgil_core.py, virgil_publish.py and
virgil_integrity.py together. Existing local configuration and build/signing wrapper remain on the maintainer machine.
No private release key or development assets belong here.

Final manifests are rebuilt from staged Git objects: exact casing, immutable SHA and exact blob size. Missing
files, duplicate IDs, ambiguous JSON and incomplete snapshots are rejected. Size caches are keyed by immutable
blob SHA in .git/virgil-object-sizes.json; cold validation uses one batch Git query.
Uploads go through a temporary ref; main moves atomically only after the complete HEAD passes validation.
Interrupted uploads leave main unchanged. A later retry may create a fresh temporary ref; stale upload refs can
be removed by the maintainer after confirming no publication is running. Pre-existing staged edits block publishing.
Unpublished directories are preserved by full-mirror synchronization. Per-folder publishing retains other folders.

For offline regression tests run `python tests/test_integrity.py` with Git installed. Tests use disposable local
repositories and a bare local remote; no GitHub credentials or real patches are required.
