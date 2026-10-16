# Publishing an updater upgrade

1. Increase the assembly/file/informational versions in `VirgilUpdater.cs`, the user-agent, application manifest
   and user guidance. Automatic upgrades require a strictly newer four-component assembly version.
2. Run the security and self-update tests. Keep the self-update protocol and startup confirmation compatible.
3. Run `./build.ps1 -SigningKeyPath <private-DPAPI-key-file>` from `updater`, using the Windows account that owns
   the release key. The maintainer's local `BuildUpdater.ps1` wrapper supplies the configured key path.
4. Commit/push the new EXE, `VirgilUpdater.zip`, ZIP checksum, `updater-release.json`, source and guidance together.
   Existing self-updating clients will verify the signature and install the newer EXE at their next check.

The build verifies the private key against `release-public-key.xml` and the public key embedded in `SelfUpdate.cs`.
It rejects a release version that does not exceed the currently committed manifest. `-CompileOnly` is for local
development and does not create a release signature or package. Normal game patch publishing preserves the
existing updater release manifest; it does not require signing or rebuild the updater.

Keep the private key outside this repository. It is protected with Windows DPAPI for the maintainer's current
user account, plus a restricted file ACL. Losing access to this account/key prevents signing releases trusted by
installed clients; retain an appropriate secured account/key recovery backup. Never commit the private key,
decrypt it into this repository, or upload it with a release. Public keys are safe to publish.

To rotate keys, first ship a release signed by the old key that embeds the new trusted public key. Do not replace
the public key in metadata alone. The application pins its trusted key; unauthenticated key changes are refused.

Staging/backups are in a hidden `.virgil-update-<id>` folder beside the EXE. `result.log` records the upgrade result;
`previous.exe` is retained after success. A failed startup restores the old EXE and records the failed payload hash
beside it. A genuinely newer/different signed release can be attempted later. No downloaded game asset is executed.
