# Matching Virgil publisher (1.5.2)

These scripts mirror the maintainer's E-drive publisher. Keep virgil_core.py, virgil_publish.py and
virgil_integrity.py and virgil_versions.py together. Existing local configuration and build/signing wrapper remain on the maintainer machine.
No private release key or development assets belong here.

Final manifests are rebuilt from staged Git objects: exact casing, immutable SHA and exact blob size. Missing
files, duplicate IDs, ambiguous JSON and incomplete snapshots are rejected. Size caches are keyed by immutable
blob SHA in .git/virgil-object-sizes.json; cold validation uses one batch Git query.
Uploads go through a temporary ref; main moves atomically only after the complete HEAD passes validation.
Interrupted uploads leave main unchanged. A later retry may create a fresh temporary ref; stale upload refs can
be removed by the maintainer after confirming no publication is running. Pre-existing staged edits block publishing.
Unpublished directories are preserved by full-mirror synchronization. Per-folder publishing retains other folders.

For offline regression tests run `python -B -m unittest discover -s tests -v` with Git installed. Tests use disposable local
repositories and a bare local remote; no GitHub credentials or real patches are required.


Online releases use stable installation keys plus a separate version/display name. Baseline is 5.0.0; each changed publication advances the fourth component by one. No-op or completed-commit retries do not advance it. Source folder names and source patch.json are untouched. Normal online publishing fetches GitHub main before numbering and refuses divergent pending histories.

The optional E-drive repository.json selects the Git mirror independently of published.json source folders. Sparse mirrors retain unchanged assets as immutable Git blobs; release staging moves those blob references to the strictly derived versioned online folder, hashes every new/changed mirror asset, and rebuilds the final index from staged objects. The complete-snapshot integrity gate still precedes every push to main. Case-only differences in source metadata preserve exact published Git casing and cannot consume endless revisions.

If the client migrates a publisher source to its fixed `(5.0.0)` folder, missing legacy source paths resolve to that sibling automatically. The publisher retains the original catalog key and Classic/HD alternative boundaries; it does not create a second online package or rewrite published.json just because a source folder moved.
