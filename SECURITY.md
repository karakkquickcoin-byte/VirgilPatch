# Security policy and release 1.3.0

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

The release uses HTTPS and pins all requests in an update to one commit. It refuses redirects and other repositories.
This relies on the security of GitHub, the maintainer account and the build machine. Blob SHA-1 checks detect
transfer/content mismatches; they do not authenticate the maintainer independently. This release is unsigned.
No code-signing key is bundled. Protect the GitHub account with strong authentication and restrict repository write access.

Game asset parsers, a compromised machine, and concurrent filesystem changes by another process with the same
user permissions are outside this updater's security boundary. The path checks are defense in depth, not an OS sandbox.
Clean local tests do not prove an antivirus report is false or establish client runtime acceptance.
