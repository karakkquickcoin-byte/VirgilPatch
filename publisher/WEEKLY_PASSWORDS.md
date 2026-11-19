# Weekly encrypted patch publishing

Virgil Publisher now encrypts the patch payloads, asset names and patch catalog before GitHub receives them. The updater program and its signed automatic upgrade channel remain public.

- In Publisher, use **Set weekly password**. Enter a fresh code and confirm it. A small label shows **Pass 0.1**, **Pass 0.2**, etc. Each code lasts seven days from when you set it. An expired code blocks future publishing until replaced; already published snapshots remain downloadable using their own code.
- Continue using each folder's normal Push button. The first encrypted publication converts all currently published source folders into one complete encrypted snapshot. Later folder pushes preserve the other packages. Classic and TexturePatch remain independent.
- Developers and players click Update in the new updater and enter that week's code. The dialog shows the matching Pass number in smaller text. Passwords are held only for that running updater session.
- Your local password history and recovery password are in `E:\M2FX_Work\virgil\weekly-passwords.json`. This private file is deliberately outside the GitHub repository. Keep a private backup; do not share or upload it. The recovery code is entered in the same updater password box.
- New/changed files receive fresh random keys. An old code cannot decrypt the new catalog or newly changed assets. Unchanged encrypted objects can be reused. Files that someone already downloaded, old public Git history and content visible inside the running game cannot be made secret retroactively.
- CLI updates read the code from `VIRGIL_WEEKLY_PASSWORD`, never a command-line argument. Passwordless legacy patch sources still work for the existing updater regression harness.

Implementation: random 64-byte per-object keys split into AES-256-CBC and HMAC-SHA256 keys; encrypt-then-MAC authenticates the header, IV and ciphertext before decryption. PBKDF2-HMAC-SHA256 with 600,000 iterations and random salts wraps the catalog key independently for weekly and recovery passwords. No universal password or decryption key is embedded in public source or the executable.

Protocol: public index format 3 intentionally does not expose a legacy patch list. Old launchers fail to read that patch index rather than treating it as a list of retired patches. Publish the signed 1.5.12 updater before the first encrypted patch snapshot. Existing clients can upgrade without entering any patch password.

Cryptography references: https://cryptography.io/en/latest/hazmat/primitives/symmetric-encryption/ and https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.-ctor?view=netframework-4.8

Publisher dependency: install requirements.txt into publisher/lib or the Python environment. The installed E-drive publisher already includes its local dependency.

## 1.5.13: code on the Update button (default from 2026-11-19)

Patch files are published plain, exactly as before the weekly lock: no re-encryption, no full re-upload, and
players only download new or changed files. Each push adds a `gate` block to `index.json` with the Pass number
and salted PBKDF2-SHA256 (600000 rounds) check values for the weekly and recovery codes; the codes themselves are
never published. Updater 1.5.13 asks for the code before any download and remembers the unlocked Pass (its public
check value only) until the next Pass. This is a spoiler blocker for early Update clicks, not access control:
the raw GitHub mirror still serves the files. Older updaters ignore the gate. Full encryption (`virgil_lock.publish`)
is kept in the code but no longer used. Tests: `E:\M2FX_Work\virgil\gate_publish_smoke.py` and `updater/tests/GateTests.cs`.
