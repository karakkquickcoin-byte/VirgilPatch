# Publishing an updater upgrade

1. Increase the assembly/file/informational versions in `VirgilUpdater.cs`, the user-agent, application manifest
   and user guidance. Automatic upgrades require a strictly newer four-component assembly version.
2. Run the security and self-update tests. Keep the self-update protocol and startup confirmation compatible.
3. Run `./build.ps1 -SigningKeyPath <private-DPAPI-key-file>` from `updater`, using the Windows account that owns
   the release key. The maintainer's local `BuildUpdater.ps1` wrapper supplies the configured key path.
4. Commit/push the new EXE, `VirgilUpdater.zip`, ZIP checksum, `updater-release.json`, source and guidance together.
   Existing self-updating clients verify the signature and install the newer EXE. 1.4.4 and later check at launch; 1.4.2/1.4.3 check on Update. Patch files require an explicit Update, selection and confirmation.

The build verifies the private key against `release-public-key.xml` and the public key embedded in `SelfUpdate.cs`.
It rejects a release version that does not exceed the currently committed manifest. `-CompileOnly` is for local
development and does not create a release signature or package. Normal game patch publishing preserves the
existing updater release manifest; it does not require signing or rebuild the updater.

The Chronicles of Lordaeron launcher embeds `assets/lordaeron-background.png` as
`VirgilPatch.LauncherBackground`. Keep this resource in all builds, including regression-test builds that create
the window. An automatic upgrade downloads only the EXE, so its interface must not depend on sidecar artwork.
Keep the assembly filename/identity `VirgilUpdater` and the existing state directory for installed clients.

Keep the private key outside this repository. It is protected with Windows DPAPI for the maintainer's current
user account, plus a restricted file ACL. Losing access to this account/key prevents signing releases trusted by
installed clients; retain an appropriate secured account/key recovery backup. Never commit the private key,
decrypt it into this repository, or upload it with a release. Public keys are safe to publish.

To rotate keys, first ship a release signed by the old key that embeds the new trusted public key. Do not replace
the public key in metadata alone. The application pins its trusted key; unauthenticated key changes are refused.

Staging/backups are in a hidden `.virgil-update-<id>` folder beside the EXE. `result.log` records the upgrade result;
`previous.exe` is retained after success. A failed startup restores the old EXE and records the failed payload hash
beside it. A genuinely newer/different signed release can be attempted later. No downloaded game asset is executed.

The ambience track is embedded as VirgilPatch.LauncherAmbience from assets/lordaeron-ambience.mp3.
Compile LauncherAmbience.cs and reference Windows' .NET Framework WPF PresentationCore/WindowsBase assemblies.
Keep the complete EXE below the existing 16 MiB signed-channel limit, which older clients enforce too.
The build now checks this before signing. Do not raise the channel limit to accommodate large media.

For 1.5.1 compile all eight root C# source files, including LauncherVideo.cs, PatchSelection.cs and StrictJson.cs. Run recovery,
selection/preview, security, launcher, audio and self-update regression checks before signing. Keep the E-drive
publisher's virgil_publish.py and virgil_integrity.py in sync with publisher/ in the release repository.
Publisher changes do not require altering development patch assets. Restart an already-running publisher to load them.

The video background is embedded as VirgilPatch.LauncherVideo from assets/lordaeron-leaves.mp4.
Compile with System.Xaml.dll as well as the existing WPF references. Use H.264/yuv420p, 1000x466, 24 fps,
an integer-second seamless loop and no audio track. Keep the matching still image in LauncherBackground.
Never add videos to the game-asset allowlist or accept a video URL from game manifests. The video cache uses
verified embedded bytes and a lifetime read lock. Native decode/render failures must retain the still backdrop
and leave updater behavior usable. Test looping, cache tampering, pause/minimize, close, and old helper upgrades.
The existing 16 MiB signed-channel limit and release-version guard must not be raised or bypassed for media.

The 1.5.1 handoff-file writer publishes ready/started.ok only after writing and closing a temporary file,
then atomically moving it into place. Keep CreateNew/no-overwrite semantics, path validation, nonce contents,
signature checks and rollback protocol intact. Old helpers poll these files; publishing an open file can cause
an unnecessary rollback on a sharing violation. This compatibility fix is independent of video playback.

Handoff readers retry only Windows sharing/lock violations within the existing 15/45-second deadlines.
They recheck paths and exact nonce content and reject oversized records. Other I/O/validation failures still fail.
