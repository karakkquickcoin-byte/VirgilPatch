"""VirgilPatch shared helpers: Epsilon patch I/O, FileDataID bookkeeping, M2/.skel reference rewriting, git blob hashes.

An Epsilon patch is a folder of flat files plus patch.json {"name","version","url","files":[{"file","id"}]}; zipped
*.patch files carry the same layout. FileDataIDs above the community listfile max are custom; at or below it they
override a game file.
"""
import os, re, json, glob, struct, hashlib, zipfile

PATCHES = r"D:\D-Fix\Epsilon\Epsilon\_retail_\Patches"
MAIN_NAME = "!Chronicles-TexturePatch(3.2.0)"
MAIN = os.path.join(PATCHES, MAIN_NAME)
RETIRED = os.path.join(os.path.dirname(PATCHES), "Patches_merged")      # merged source patches are moved here
REPO = r"D:\D-Fix\VirgilPatch"
REPO_PATCHES = os.path.join(REPO, "patches")            # one sub-folder per published patch, named as installed
WORK = r"E:\M2FX_Work\virgil"
PUBLISHED = os.path.join(WORK, "published.json")        # which local patch folders go to GitHub (first = main)
BACKUPS = os.path.join(WORK, "backups")
CLIENT_DATA = r"D:\D-Fix\Epsilon\Epsilon\Data"
CASC_TOOLS = r"D:\D-Fix\ForeverLightingPort\tools"
AUDIT_LODS = r"E:\M2FX_Work\Claude\plaguewood\scripts\audit_lods.py"
FORGE_LEDGER = (r"D:\D-Fix\todo\Updated WoW Pipeline\M2_LOD_Forge_v0.6.1_highest_plus_one_ids"
                r"\M2_LOD_Forge_v0.6.1_highest_plus_one_ids\Global_ID_Ledger.json")
UP_LEDGERS = [r"D:\D-Fix\todo\Updated WoW Pipeline\Upports2\local_ids.csv",
              r"E:\M2FX_Work\temp\upports_amalgam\local_ids.csv", r"E:\M2FX_Work\temp\upports_run\local_ids.csv"]
LISTFILE_MAX = 8_284_175          # 12.x community listfile max: new ids start above it
BAND_MAX = 8_345_012              # rar1 starts at 8,345,013

# These are independently published alternatives. Epsilon controls which one
# is enabled; publishing both must not renumber their shared FileDataIDs.
EXCLUSIVE_PUBLISH_GROUPS = (
    frozenset({"chronicles-classic", "!chronicles-texturepatch(3.2.0)"}),
)


def exclusive_alternatives(first, second):
    """True only for two different, explicitly named alternative packages."""
    a = os.path.basename(os.path.normpath(first)).casefold()
    b = os.path.basename(os.path.normpath(second)).casefold()
    return a != b and any(a in group and b in group for group in EXCLUSIVE_PUBLISH_GROUPS)

# chunks whose payload is a plain uint32 FileDataID array
ID_ARRAYS = {b"SFID": "skin", b"TXID": "texture", b"BFID": "bone", b"SKID": "skel", b"PFID": "phys",
             b"GPID": "model", b"RPID": "model"}



# Windows path rules are enforced before reading imported files or writing mirrors.
ASSET_EXTENSIONS = {'.adt', '.anim', '.blp', '.bone', '.db2', '.m2', '.ogg', '.phys', '.skel', '.skin', '.wdl', '.wdt', '.wmo'}


def safe_relative(name, asset=False, folder=False):
    import ntpath
    if not isinstance(name, str) or not name or len(name) > 220 or '\\' in name or ntpath.isabs(name):
        raise ValueError('unsafe relative path: %r' % name)
    for part in name.split('/'):
        if (not part or part in ('.', '..') or part.endswith(('.', ' ')) or
                any(ord(c) < 32 or ord(c) == 127 or c in '<>:\"|?*' for c in part) or part.casefold() == '.git' or
                re.fullmatch(r'(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])', part.split('.')[0], re.I)):
            raise ValueError('unsafe Windows path component: %r' % name)
    if folder and '/' in name:
        raise ValueError('patch name must be one folder')
    if asset and os.path.splitext(name)[1].casefold() not in ASSET_EXTENSIONS:
        raise ValueError('unsupported game asset type: %r' % name)
    return name


def no_links(path):
    path = os.path.abspath(path)
    while True:
        try:
            st = os.lstat(path)
            if os.path.islink(path) or getattr(st, 'st_file_attributes', 0) & 0x400:
                raise ValueError('symbolic links and junctions are not supported: ' + path)
        except FileNotFoundError:
            pass
        parent = os.path.dirname(path)
        if path == parent:
            break
        path = parent


def tree(root):
    """{lower relative path: (relative path, size, mtime_ns)} for every file under root, from ONE directory walk.
    Same guarantee as no_links() on each file, at a fraction of the cost: the root's ancestors are checked once and
    every directory/file met during the walk is rejected if it is a symbolic link or junction (Windows directory
    listings carry the reparse flag, size and time, so no per-file system calls are needed)."""
    no_links(root)
    out, stack = {}, [("", root)]
    while stack:
        rel, d = stack.pop()
        with os.scandir(d) as it:
            for e in it:
                st = e.stat(follow_symlinks=False)
                if e.is_symlink() or getattr(st, 'st_file_attributes', 0) & 0x400:
                    raise ValueError('symbolic links and junctions are not supported: ' + e.path)
                r = rel + "/" + e.name if rel else e.name
                if e.is_dir(follow_symlinks=False):
                    stack.append((r, e.path))
                else:
                    out[r.lower()] = (r, st.st_size, st.st_mtime_ns)
    return out


def path_in(root, name):
    safe_relative(name)
    base = os.path.abspath(root)
    target = os.path.abspath(os.path.join(base, name.replace('/', os.sep)))
    if os.path.normcase(os.path.commonpath([base, target])) != os.path.normcase(base) or target == base:
        raise ValueError('path escapes its root')
    no_links(target)
    return target


def validate_entries(entries):
    if not isinstance(entries, list):
        raise ValueError('patch files must be a list')
    seen = set()
    for entry in entries:
        if not isinstance(entry, dict):
            raise ValueError('invalid patch entry')
        name = safe_relative(entry.get('file'), asset=True)
        key = name.casefold()
        if key in seen:
            raise ValueError('duplicate patch filename: ' + name)
        seen.add(key)
        fid = entry.get('id')
        if type(fid) is not int or not 0 < fid <= 0xffffffff:
            raise ValueError('invalid FileDataID: ' + name)
    for name in seen:
        parts = name.split('/')
        if any('/'.join(parts[:i]) in seen for i in range(1, len(parts))):
            raise ValueError('patch file is also a directory: ' + name)


# ---------------------------------------------------------------- patches
class Patch:
    """A patch folder or zipped .patch: meta, entries and file access by entry name."""

    def __init__(self, path):
        no_links(path)
        self.path = path
        self.is_zip = os.path.isfile(path)
        if self.is_zip:
            self.zip = zipfile.ZipFile(path)
            names = self.zip.namelist()
            for name in names:
                safe_relative(name.rstrip('/'))
            if len({n.casefold() for n in names}) != len(names):
                raise ValueError('duplicate archive paths')
            pj = [n for n in names if n.rsplit('/', 1)[-1].casefold() == 'patch.json']
            if len(pj) != 1:
                raise ValueError('archive must contain exactly one patch.json')
            if self.zip.getinfo(pj[0]).file_size > 16 * 1024 * 1024:
                raise ValueError('patch metadata exceeds size limit')
            self.prefix = pj[0][:-len("patch.json")]
            self.meta = json.loads(self.zip.read(pj[0]).decode("utf-8-sig"))
            self._zip_names = {n[len(self.prefix):].lower(): n for n in names if n.startswith(self.prefix)}
        else:
            with open(path_in(path, "patch.json"), encoding="utf-8-sig") as handle:
                self.meta = json.load(handle)
        self.entries = self.meta.get("files", [])
        validate_entries(self.entries)          # every name is a safe relative path (no .., drive, backslash ...)
        # one walk replaces a link check per entry per ancestor folder (that was ~3 M system calls on big patches)
        self.tree = None if self.is_zip else tree(path)
        self.name = os.path.basename(path.rstrip("\\/"))

    def stat(self, name):
        """(size, mtime_ns) of a listed file from the walk, or None when it is missing."""
        hit = self.tree.get(name.lower()) if self.tree is not None else None
        return (hit[1], hit[2]) if hit else None

    def has(self, name):
        safe_relative(name, asset=True)
        if self.is_zip:
            return name.lower() in self._zip_names
        return name.lower() in self.tree

    def read(self, name):
        safe_relative(name, asset=True)
        if self.is_zip:
            return self.zip.read(self._zip_names[name.lower()])
        with open(path_in(self.path, name), "rb") as f:
            return f.read()

    def files_on_disk(self):
        if self.is_zip:
            return [n for n in self._zip_names if n and not n.endswith("/")]
        return [v[0] for v in self.tree.values()]


def load_published():
    """Absolute paths of the patch folders published to GitHub; the first one is the main patch."""
    try:
        lst = [p for p in json.load(open(PUBLISHED, encoding="utf-8"))["patches"] if isinstance(p, str)]
    except (OSError, ValueError, KeyError, TypeError):
        lst = []
    return lst or [MAIN]


def save_published(paths):
    os.makedirs(WORK, exist_ok=True)
    with open(PUBLISHED, "w", encoding="utf-8") as f:
        json.dump({"patches": paths}, f, indent=1)


def list_files(folder):
    """Every file under folder, relative, with forward slashes (patches may use sub-folders); links rejected."""
    return [v[0] for v in tree(folder).values()]


def write_patch_json(folder, meta, entries):
    import tempfile
    validate_entries(entries)
    no_links(folder)
    dest = path_in(folder, 'patch.json')
    meta = dict(meta)
    meta["files"] = entries
    fd, tmp = tempfile.mkstemp(prefix='patch.json.', suffix='.virgil_tmp', dir=folder)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as f:
            json.dump(meta, f, separators=(",", ":"))
        no_links(dest)
        os.replace(tmp, dest)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


ID_RE = re.compile(r'"file"\s*:\s*"([^"]*)"\s*,\s*"id"\s*:\s*(\d+)|"id"\s*:\s*(\d+)\s*,\s*"file"\s*:\s*"([^"]*)"')


def _pairs(txt):
    for m in ID_RE.finditer(txt):
        yield (m.group(1) or m.group(4)), int(m.group(2) or m.group(3))


def installed_patches(exclude=()):
    """{source name: {id: file}} for every other installed patch folder and zipped .patch."""
    exclude = {os.path.normcase(os.path.abspath(e)) for e in exclude}
    out = {}
    for pj in glob.glob(os.path.join(PATCHES, "*", "patch.json")):
        d = os.path.dirname(pj)
        if os.path.normcase(os.path.abspath(d)) in exclude:
            continue
        out[os.path.basename(d)] = {i: f for f, i in _pairs(open(pj, encoding="utf-8-sig", errors="replace").read())}
    for zp in glob.glob(os.path.join(PATCHES, "*.patch")):
        if os.path.normcase(os.path.abspath(zp)) in exclude:
            continue
        try:
            with zipfile.ZipFile(zp) as z:
                for n in z.namelist():
                    if n.lower().endswith("patch.json"):
                        out[os.path.basename(zp)] = {i: f for f, i in
                                                     _pairs(z.read(n).decode("utf-8-sig", errors="replace"))}
        except zipfile.BadZipFile:
            pass
    return out


def alternate_copies(main_entries, others):
    """Other installed patches that are just another version of the main patch (e.g. the zipped 3.3.3 build): they share
    nearly every {file, id} pair, so their overlap with the main patch is not a collision."""
    pairs = {(e["file"].lower(), e["id"]) for e in main_entries}
    alt = set()
    for src, ids in others.items():
        if len(ids) > 100:
            same = sum(1 for i, f in ids.items() if (f.lower(), i) in pairs)
            if same >= 0.9 * min(len(ids), len(pairs)):
                alt.add(src)
    return alt


def ledger_ids():
    ids = {}
    try:
        for e in json.load(open(FORGE_LEDGER, encoding="utf-8")).get("files", []):
            ids.setdefault(int(e["id"]), "forge ledger: %s" % e.get("file", ""))
    except (OSError, ValueError, KeyError, TypeError):
        pass
    for led in UP_LEDGERS:
        if os.path.exists(led):
            for line in open(led, encoding="utf-8", errors="replace"):
                m = re.match(r"\s*(\d+)\s*;(.*)", line)
                if m:
                    ids.setdefault(int(m.group(1)), "upports ledger: " + m.group(2).strip())
    return ids


class IdAllocator:
    """New ids: highest id already used anywhere in (listfile max, band max] + 1, never anything in `used`."""

    def __init__(self, used):
        self.used = set(used)
        self.next = max([LISTFILE_MAX] + [v for v in self.used if LISTFILE_MAX < v <= BAND_MAX]) + 1

    def take(self):
        while self.next in self.used:
            self.next += 1
        if self.next > BAND_MAX:
            raise RuntimeError("custom id band exhausted")
        v = self.next
        self.used.add(v)
        self.next += 1
        return v


# ---------------------------------------------------------------- chunked files (.m2 MD21 / .skel)
def chunk_list(raw):
    """[(tag, payload offset, size)] or [] when the file is not chunked."""
    out, i = [], 0
    if raw[:4] not in (b"MD21", b"SKL1", b"SKB1", b"SKA1", b"SKS1", b"SKPD", b"AFID", b"BFID"):
        return out
    while i + 8 <= len(raw):
        tag, size = raw[i:i + 4], struct.unpack_from("<I", raw, i + 4)[0]
        if i + 8 + size > len(raw):
            break
        out.append((tag, i + 8, size))
        i += 8 + size
    return out


def file_refs(raw):
    """[(kind, fid, absolute byte offset)] for every FileDataID a chunked .m2/.skel references."""
    refs = []
    for tag, off, size in chunk_list(raw):
        if tag in ID_ARRAYS:
            for k in range(size // 4):
                refs.append((ID_ARRAYS[tag], struct.unpack_from("<I", raw, off + 4 * k)[0], off + 4 * k))
        elif tag == b"AFID":                                  # {u16 anim, u16 sub, u32 fid}
            for k in range(size // 8):
                refs.append(("anim", struct.unpack_from("<I", raw, off + 8 * k + 4)[0], off + 8 * k + 4))
        elif tag == b"SKPD" and size >= 12:                   # parent skeleton
            refs.append(("skel", struct.unpack_from("<I", raw, off + 8)[0], off + 8))
    return [r for r in refs if r[1]]


def rewrite_ids(raw, mapping):
    """Copy of raw with every referenced FileDataID in `mapping` replaced; (data, number of rewrites)."""
    out, n = bytearray(raw), 0
    for kind, fid, at in file_refs(raw):
        if fid in mapping:
            struct.pack_into("<I", out, at, mapping[fid])
            n += 1
    return bytes(out), n


def blp_ok(raw):
    if raw[:4] != b"BLP2":
        return raw[:4] == b"BLP1"
    if len(raw) < 148:
        return False
    offs = struct.unpack_from("<16I", raw, 20)
    lens = struct.unpack_from("<16I", raw, 84)
    return all(not o or o + l <= len(raw) for o, l in zip(offs, lens)) and offs[0] != 0


def git_blob_sha(path):
    h = hashlib.sha1()
    h.update(b"blob %d\0" % os.path.getsize(path))
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def client_root():
    """FileDataIDs the Epsilon 9.2.7 client ships (CASC root)."""
    import sys
    if CASC_TOOLS not in sys.path:
        sys.path.insert(0, CASC_TOOLS)
    import casc
    return casc.LocalCasc(CLIENT_DATA, build_name_contains="45745patch9.2.7").root


def wow_running():
    import subprocess
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Wow.exe", "/NH"], capture_output=True, text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout
        return "wow.exe" in out.lower()
    except OSError:
        return False
