# Security policy and release 1.4.0

Report an antivirus detection with its exact URL, ZIP SHA-256, detected file path and antivirus definition version.
Keep detected files blocked while they are investigated. Do not use exclusions as a workaround.
The maintainer can submit a sample at https://www.microsoft.com/en-us/wdsi/filesubmission.

The updater installs only .adt, .anim, .blp, .bone, .db2, .m2, .ogg, .phys, .skel, .skin, .wdl, .wdt and .wmo
game assets, plus its validated patch.json metadata. New asset formats need an explicit reviewed allowlist change.
Executable programs, libraries, scripts and shortcut files are not supported update payloads.

Manifest paths, imported publisher filenames and old updater state are untrusted input.
Absolute paths, traversal, alternate data streams, Windows device names, ambiguous destination names,
and symbolic links/junctions are rejected. Download sizes are bounded, checksums are verified before atomic
file replacement, and deletion candidates are rehashed at use time. Individual files are replaced atomically;
an interrupted multi-file update can still require another update. Close WoW before updating.

All assets are downloaded individually only when missing or changed; repository archive downloads are not supported.
The release uses HTTPS and pins all requests in an update to one commit. It refuses redirects and other repositories.
This relies on the security of GitHub, the maintainer account and the build machine. Blob SHA-1 checks detect
transfer/content mismatches; they do not authenticate the maintainer independently. The initial EXE is not Authenticode-signed; program-upgrade authentication is described below.
No code-signing key is bundled. Protect the GitHub account with strong authentication and restrict repository write access.

Game asset parsers, a compromised machine, and concurrent filesystem changes by another process with the same
user permissions are outside this updater's security boundary. The path checks are defense in depth, not an OS sandbox.
Clean local tests do not prove an antivirus report is false or establish client runtime acceptance.


## Program upgrade trust

Program upgrades use a separate `updater-release.json` channel, not the game asset index. The manifest's version,
fixed EXE path, byte size and SHA-256 are covered by an RSA-4096/SHA-256 signature verified against an embedded
public key. The private release key is not in this repository. Same-version/downgrade releases are ignored.
Only a verified .NET updater with matching assembly identity/version can be launched. Raw HTTPS URLs stay
within the configured GitHub repository and are pinned to one commit.

The helper verifies its current EXE/parent identity, waits for the old process to exit, replaces the EXE atomically
on the same volume and keeps a backup. The new window confirms startup; a failed start triggers rollback and
suppresses repeated attempts of the same failed release. This is startup acceptance, not proof that every new
program feature is correct. Signing-key theft or compromise of the initial installed EXE remains outside this trust boundary.
No privilege elevation, antivirus changes or silent executable updates through game patch entries are supported.
The initial download is not Authenticode-signed, and release signatures do not remove antivirus warnings.

## Recovery and publication consistency (1.5.0)

Patch selection limits local scanning, writes and removals to selected folders. Metadata is always rehashed;
legacy asset caches are reverified once, with optional Deep repair thereafter. JSON rejects duplicate decoded
keys, invalid UTF-8, non-JSON syntax and excessive nesting; IDs, paths, hashes and integer sizes are validated.
Each patch's metadata is validated before its asset transfers and installed only after the patch verifies.
Completed patches are finalized independently and successful assets survive a retry. Individual replacement is
atomic; the whole patch is not a transactional directory replacement. Stop WoW before modifying its files.

The matching publisher builds the index from staged Git blobs with exact case-sensitive paths, hashes and sizes.
Immutable blob-size caching accelerates repeated checks. Batched uploads use a temporary Git ref, then atomically
promote the validated complete snapshot to main. Existing staged changes block patch publication; unrelated
unpublished mirror directories are preserved. Keep publisher and updater source changes together.
