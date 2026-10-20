"""VirgilPatch publisher - put patch folders on GitHub, merge new content into them, check, push.

  py -3.10 virgil_publish.py                       GUI
  py -3.10 virgil_publish.py list                  patch folders published to GitHub
  py -3.10 virgil_publish.py scan                  what changed in each published folder (fast)
  py -3.10 virgil_publish.py push-folder <name>    push ONE folder's changes [--deep]
  py -3.10 virgil_publish.py new-folder <folder>   put a folder on GitHub and push it
  py -3.10 virgil_publish.py remove-folder <name>  take a folder off GitHub and push that
  py -3.10 virgil_publish.py add <patch folder>    publish another patch folder (as its own folder)
  py -3.10 virgil_publish.py remove <name>         stop publishing a patch folder (local folder untouched)
  py -3.10 virgil_publish.py merge <patch folder or .patch> [--into NAME] [--override replace|skip] [--retire] [--dry-run]
  py -3.10 virgil_publish.py check [--all]
  py -3.10 virgil_publish.py publish [--no-push] [-m MESSAGE]
  py -3.10 virgil_publish.py push                  push commits left over from an interrupted publish
  py -3.10 virgil_publish.py undo [<backup folder>]

GitHub carries a list of patch folders (E:\\M2FX_Work\\virgil\\published.json, first = the Chronicles texture patch);
the updater installs each as Patches\\<folder name>. Classic and TexturePatch are independently published
alternatives; the client controls which is enabled. Other packages retain custom-ID collision checks.

Merge rules (the target patch.json is the bible):
  * same file name + same id as the target            -> update in place (same id)
  * new file, id unused everywhere                    -> added under its own id
  * custom id (above the listfile max) already used by a different file in the target or any other installed
    patch -> renumbered to a fresh id (highest used in the custom band + 1) and every .m2/.skel of the incoming patch
    that referenced it is rewritten (SFID/TXID/AFID/BFID/SKID/PFID/GPID/RPID/SKPD)
  * game-file override (id at/below the listfile max) that the target already overrides with a different file ->
    conflict: --override replace (incoming wins) or skip (target keeps its file); the GUI asks
  * file name already used by a different id -> incoming file renamed <stem>_<id><ext>
Every merge is journaled under E:\\M2FX_Work\\virgil\\backups\\<time> and rolled back automatically if the check fails.
"""
import os, sys, json, time, shutil, struct, argparse, subprocess, datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import virgil_core as vc
import virgil_integrity as vi
import virgil_versions as vv

HASH_CACHE = os.path.join(vc.WORK, "hash_cache.json")
_hash_cache = None
_hash_dirty = False
_index_cache = {}


def log(*a):
    print(*a, flush=True)


def _mk(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    return path


def _norm(p):
    return os.path.normcase(os.path.abspath(p))


def pname(path):
    return vc.safe_relative(os.path.basename(os.path.normpath(path)), folder=True)


# ---------------------------------------------------------------- hashing with a size/mtime cache
def blob_sha(path, size_mtime=None):
    """git blob sha of a file; reused from the cache while its size and modified time are unchanged. size_mtime can
    come from a directory walk (vc.Patch.stat) so a cache hit needs no system call at all."""
    global _hash_cache, _hash_dirty
    if _hash_cache is None:
        try:
            with open(HASH_CACHE, encoding="utf-8") as f:
                _hash_cache = json.load(f)
        except (OSError, ValueError):
            _hash_cache = {}
    if size_mtime is None:
        st = os.stat(path)
        size_mtime = (st.st_size, st.st_mtime_ns)
    key = os.path.normcase(os.path.abspath(path))
    hit = _hash_cache.get(key)
    if hit and hit[0] == size_mtime[0] and hit[1] == size_mtime[1]:
        return hit[2]
    sha = vc.git_blob_sha(path)
    _hash_cache[key] = [size_mtime[0], size_mtime[1], sha]
    _hash_dirty = True
    return sha


def save_hash_cache():
    """Written only when something was hashed (the cache is ~17 MB; rewriting it per folder cost seconds), via a
    temporary file so an interrupted save never leaves a broken cache."""
    global _hash_dirty
    if _hash_cache is None or not _hash_dirty:
        return
    os.makedirs(vc.WORK, exist_ok=True)
    tmp = HASH_CACHE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(_hash_cache, f, separators=(",", ":"))
    os.replace(tmp, HASH_CACHE)
    _hash_dirty = False


def published_index():
    """index.json as last committed (the working-tree copy is rewritten by every sync), as {"patches": {name: {...}}}."""
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    head = subprocess.run(["git", "-c", "safe.directory=" + os.path.abspath(vc.REPO), "-C", vc.REPO, "rev-parse", "HEAD"], capture_output=True, creationflags=flags).stdout.strip()
    if head and head in _index_cache:                         # same commit: reuse the parsed index (it is several MB)
        return json.loads(_index_cache[head])
    r = subprocess.run(["git", "-c", "safe.directory=" + os.path.abspath(vc.REPO), "-C", vc.REPO, "show", "HEAD:index.json"], capture_output=True, creationflags=flags)
    if r.returncode:
        return None
    try:
        idx = json.loads(r.stdout.decode("utf-8"))
    except ValueError:
        return None
    if head:
        _index_cache.clear()
        _index_cache[head] = r.stdout.decode("utf-8")
    if "patches" not in idx:                                  # format 1: the single main patch under patch/
        idx = {"format": 1, "patches": {idx.get("patch_folder", vc.MAIN_NAME): idx}}
    return idx


def patch_ids(path):
    return {e["id"]: e["file"] for e in vc.Patch(path).entries}


def published_collisions(path, published):
    """[(id, our file, other published patch, their file)] for CUSTOM ids (above the listfile max) that another
    compatible published patch uses for a different file. Explicitly exclusive alternatives are exempt.
    Two patches overriding the same game file, or carrying the same file under the same id, is allowed (that is how
    the patches already sit side by side in Patches)."""
    mine = patch_ids(path)
    out = []
    for other in published:
        if (_norm(other) == _norm(path) or vc.exclusive_alternatives(path, other)
                or not os.path.isfile(os.path.join(other, "patch.json"))):
            continue
        theirs = patch_ids(other)
        for i in sorted(set(mine) & set(theirs)):
            if i > vc.LISTFILE_MAX and mine[i].lower() != theirs[i].lower():
                out.append((i, mine[i], pname(other), theirs[i]))
    return out


# ---------------------------------------------------------------- merge
def plan_merge(incoming_path, main_path=vc.MAIN):
    if _norm(incoming_path) == _norm(main_path):
        raise ValueError("that is the target patch itself - pick the patch whose content you want to ADD into it "
                         "(e.g. Patches/aaa1b). To put a whole folder on GitHub as it is, use 'Add folder to GitHub'.")
    inc, main = vc.Patch(incoming_path), vc.Patch(main_path)
    m_name = {e["file"].lower(): e for e in main.entries}
    m_id = {e["id"]: e for e in main.entries}
    others = vc.installed_patches(exclude=[main_path, incoming_path])
    alt = vc.alternate_copies(main.entries, others)
    other_ids = {}
    used = set(m_id) | set(vc.ledger_ids())
    for src, ids in others.items():
        used |= set(ids)
        if src not in alt and not vc.exclusive_alternatives(main_path, src):
            for i, f in ids.items():
                other_ids.setdefault(i, []).append((src, f))
    used |= {int(e["id"]) for e in inc.entries}
    alloc = vc.IdAllocator(used)
    actions, remap, taken = [], {}, set(m_name)
    for e in inc.entries:
        n, i = e["file"], int(e["id"])
        a = {"src": n, "dst": n, "old_id": i, "id": i, "note": ""}
        if not inc.has(n):
            a["kind"] = "missing"
            actions.append(a)
            continue
        same_name, same_id = m_name.get(n.lower()), m_id.get(i)
        if same_name is not None and same_name["id"] == i:
            a["kind"] = "update"
        else:
            if same_id is not None:
                if i <= vc.LISTFILE_MAX:
                    a["kind"], a["main_file"] = "override_conflict", same_id["file"]
                    a["note"] = "the target already overrides game file %d with %s" % (i, same_id["file"])
                else:
                    a["kind"], a["id"] = "renumber", alloc.take()
                    a["note"] = "id %d is the target's %s" % (i, same_id["file"])
            elif i > vc.LISTFILE_MAX and i in other_ids and not any(f.lower() == n.lower() for _, f in other_ids[i]):
                a["kind"], a["id"] = "renumber", alloc.take()
                a["note"] = "id %d is used by %s" % (i, ", ".join("%s (%s)" % sf for sf in other_ids[i][:3]))
            else:
                a["kind"] = "add"
                if i <= vc.LISTFILE_MAX and i in other_ids:
                    a["note"] = "game file also overridden by " + ", ".join(s for s, _ in other_ids[i][:3])
            if a["kind"] == "renumber":
                remap[i] = a["id"]
            if n.lower() in taken:                     # name clash with a different id: keep both, rename ours
                stem, ext = os.path.splitext(n)
                dst, k = "%s_%d%s" % (stem, a["id"], ext), 2
                while dst.lower() in taken:
                    dst, k = "%s_%d_%d%s" % (stem, a["id"], k, ext), k + 1
                a["dst"] = dst
                a["note"] = (a["note"] + "; " if a["note"] else "") + "renamed (target has another %s)" % n
        taken.add(a["dst"].lower())
        actions.append(a)
    plan = {"incoming": incoming_path, "main": main_path, "actions": actions, "remap": remap,
            "alternate_copies_ignored": sorted(alt)}
    _attach_data(plan, inc, main)
    return plan


def _attach_data(plan, inc, main):
    """Final bytes for every incoming file (references renumbered); 'update' with identical bytes -> 'unchanged'."""
    for a in plan["actions"]:
        if a["kind"] == "missing":
            continue
        data = inc.read(a["src"])
        if plan["remap"] and a["src"].lower().endswith((".m2", ".skel")):
            data, a["rewrites"] = vc.rewrite_ids(data, plan["remap"])
        a["_data"] = data
        if a["kind"] == "update" and main.has(a["dst"]) and main.read(a["dst"]) == data:
            a["kind"] = "unchanged"


def summarize(plan):
    c = {}
    for a in plan["actions"]:
        c[a["kind"]] = c.get(a["kind"], 0) + 1
    lines = ["%s -> %s" % (plan["incoming"], plan["main"]),
             "  " + ", ".join("%s %d" % kv for kv in sorted(c.items()))]
    for a in plan["actions"]:
        if a["kind"] in ("renumber", "override_conflict", "missing") or a["dst"] != a["src"]:
            lines.append("  %-17s %s (%d)%s%s" % (a["kind"], a["src"], a["old_id"],
                                                  " -> %s (%d)" % (a["dst"], a["id"]) if a["kind"] != "missing" else "",
                                                  "  [%s]" % a["note"] if a["note"] else ""))
    if plan["remap"]:
        lines.append("  references rewritten in %d incoming .m2/.skel files"
                     % sum(1 for a in plan["actions"] if a.get("rewrites")))
    if plan["alternate_copies_ignored"]:
        lines.append("  (older copies of the target patch, not counted as collisions: %s)"
                     % ", ".join(plan["alternate_copies_ignored"]))
    return "\n".join(lines)


def apply_merge(plan, override="skip", retire=False):
    """Writes the plan into the target patch; journaled backup; rolls back if the check of the touched files fails."""
    main_path = plan["main"]
    for action in plan['actions']:
        vc.safe_relative(action['dst'], asset=True)
        vc.path_in(main_path, action['dst'])
    if not any(a["kind"] not in ("missing", "unchanged") and (a["kind"] != "override_conflict" or override == "replace")
               for a in plan["actions"]):
        return {"ok": True, "errors": [], "warnings": [], "nothing_to_merge": True}
    main = vc.Patch(main_path)
    bdir = os.path.join(vc.BACKUPS, time.strftime("%Y%m%d_%H%M%S"))
    os.makedirs(os.path.join(bdir, "overwritten"))
    os.makedirs(os.path.join(bdir, "removed"))
    shutil.copy2(vc.path_in(main_path, "patch.json"), vc.path_in(bdir, "patch.json"))
    journal = {"main": main_path, "incoming": plan["incoming"], "added": [], "overwritten": [], "removed": [],
               "retired_to": None}
    entries = list(main.entries)
    touched = []
    try:
        for a in plan["actions"]:
            kind = a["kind"]
            if kind in ("missing", "unchanged") or (kind == "override_conflict" and override != "replace"):
                continue
            if kind == "override_conflict":
                old = a["main_file"]
                entries = [e for e in entries if e["file"].lower() != old.lower()]
                if os.path.exists(vc.path_in(main_path, old)):
                    shutil.move(vc.path_in(main_path, old), _mk(vc.path_in(os.path.join(bdir, "removed"), old)))
                    journal["removed"].append(old)
            dst = _mk(vc.path_in(main_path, a["dst"]))
            if os.path.exists(dst):
                shutil.copy2(dst, _mk(vc.path_in(os.path.join(bdir, "overwritten"), a["dst"])))
                journal["overwritten"].append(a["dst"])
            else:
                journal["added"].append(a["dst"])
            with open(dst, "wb") as f:
                f.write(a["_data"])
            if kind != "update":
                entries.append({"file": a["dst"], "id": a["id"]})
            touched.append(a["dst"])
        vc.write_patch_json(main_path, main.meta, entries)
    finally:
        json.dump(journal, open(os.path.join(bdir, "journal.json"), "w", encoding="utf-8"), indent=1)
    # the incoming patch is about to be part of the target: don't count it as a separate published patch here
    pub = [p for p in vc.load_published() if _norm(p) != _norm(plan["incoming"])]
    rep = check(main_path, scope=touched, published=pub)
    rep["backup"] = bdir
    if rep["errors"]:
        undo(bdir)
        rep["rolled_back"] = True
        return rep
    if retire and os.path.isdir(plan["incoming"]) and \
            os.path.normcase(os.path.dirname(os.path.abspath(plan["incoming"]))) == os.path.normcase(vc.PATCHES):
        os.makedirs(vc.RETIRED, exist_ok=True)
        dst = os.path.join(vc.RETIRED, os.path.basename(plan["incoming"]))
        if os.path.exists(dst):
            dst += time.strftime("_%Y%m%d_%H%M%S")
        shutil.move(plan["incoming"], dst)
        journal["retired_to"] = dst
        json.dump(journal, open(os.path.join(bdir, "journal.json"), "w", encoding="utf-8"), indent=1)
    rep["retired_to"] = journal["retired_to"]
    # its content now lives in the target: stop publishing the merged patch on its own (it would clash)
    full = vc.load_published()
    if any(_norm(p) == _norm(plan["incoming"]) for p in full):
        vc.save_published(pub)
        rep["removed_from_github_list"] = pname(plan["incoming"])
    return rep


def undo(bdir=None):
    if bdir is None:
        dirs = sorted(d for d in os.listdir(vc.BACKUPS) if os.path.isfile(os.path.join(vc.BACKUPS, d, "journal.json"))
                      and "undone" not in json.load(open(os.path.join(vc.BACKUPS, d, "journal.json"), encoding="utf-8")))
        if not dirs:
            raise SystemExit("nothing to undo")
        bdir = os.path.join(vc.BACKUPS, dirs[-1])
    j = json.load(open(os.path.join(bdir, "journal.json"), encoding="utf-8"))
    main_path = j["main"]
    rolled = os.path.join(bdir, "rolled_back")
    os.makedirs(rolled, exist_ok=True)
    for n in j["added"]:
        if os.path.exists(vc.path_in(main_path, n)):
            shutil.move(vc.path_in(main_path, n), _mk(vc.path_in(rolled, n)))
    for n in j["overwritten"]:
        shutil.copy2(vc.path_in(os.path.join(bdir, "overwritten"), n), _mk(vc.path_in(main_path, n)))
    for n in j["removed"]:
        shutil.move(vc.path_in(os.path.join(bdir, "removed"), n), _mk(vc.path_in(main_path, n)))
    shutil.copy2(vc.path_in(bdir, "patch.json"), vc.path_in(main_path, "patch.json"))
    if j.get("retired_to") and os.path.exists(j["retired_to"]) and not os.path.exists(j["incoming"]):
        shutil.move(j["retired_to"], j["incoming"])
    j["undone"] = time.strftime("%Y-%m-%d %H:%M:%S")
    json.dump(j, open(os.path.join(bdir, "journal.json"), "w", encoding="utf-8"), indent=1)
    return bdir


# ---------------------------------------------------------------- check
def check(patch_path=vc.MAIN, scope="changed", published=None):
    """errors block a merge/publish; warnings are pre-existing or informational.
    scope: "changed" (files new/different since the last publish), "all", or a list of file names."""
    published = vc.load_published() if published is None else published
    patch = vc.Patch(patch_path)
    errors, warnings = [], []
    ents = patch.entries
    ids = [e["id"] for e in ents]
    names = [e["file"].lower() for e in ents]
    if len(ids) != len(set(ids)):
        seen, d = set(), []
        for e in ents:
            if e["id"] in seen:
                d.append(e)
            seen.add(e["id"])
        errors.append("duplicate ids in patch.json: %s" % d[:5])
    if len(names) != len(set(names)):
        errors.append("duplicate file names in patch.json")
    missing = [e["file"] for e in ents if not os.path.isfile(os.path.join(patch_path, e["file"]))]
    if missing:
        errors.append("%d listed files missing: %s" % (len(missing), missing[:5]))
    listed = set(names) | {"patch.json"}
    unlisted = [f for f in patch.files_on_disk() if f.lower() not in listed]
    if unlisted:
        warnings.append("%d files on disk are not in patch.json (not loaded, not published): %s"
                        % (len(unlisted), unlisted[:5]))
    idx = published_index()
    base = (idx or {}).get("patches", {}).get(patch.name)
    prev = (base or {}).get("files", {})
    if scope == "changed":
        if base is None:                               # first publish of this patch: pre-existing problems are warnings
            scope_names, strict = [e["file"] for e in ents], False
        else:
            scope_names = [e["file"] for e in ents if e["file"] not in prev or
                           (os.path.isfile(os.path.join(patch_path, e["file"])) and
                            prev[e["file"]].get("sha") != blob_sha(os.path.join(patch_path, e["file"])))]
            strict = True
    elif scope == "all":
        scope_names, strict = [e["file"] for e in ents], False
    else:
        scope_names, strict = list(scope), True
    scope_l = {n.lower() for n in scope_names}
    by_name = {e["file"].lower(): e for e in ents}
    prev_ids = {v.get("id") for v in prev.values()}
    # References cannot depend on the alternative package that must be disabled.
    in_patch = set(ids)
    for other in published:
        if (_norm(other) != _norm(patch_path) and not vc.exclusive_alternatives(patch_path, other)
                and os.path.isfile(os.path.join(other, "patch.json"))):
            in_patch |= set(patch_ids(other))
    if any(_norm(p) == _norm(patch_path) for p in published):
        pc = published_collisions(patch_path, published)
        if pc:
            errors.append("%d ids are also in another published patch (both get installed): %s" % (len(pc), pc[:3]))

    # collisions with other installed, unpublished patches (custom ids only; game overrides may legitimately overlap)
    others = vc.installed_patches(exclude=[patch_path] + list(published))
    alt = vc.alternate_copies(ents, others)
    for src, oid in others.items():
        if src in alt or vc.exclusive_alternatives(patch_path, src):
            continue
        clash = [(by_name[n]["file"], by_name[n]["id"], oid[by_name[n]["id"]]) for n in names
                 if by_name[n]["id"] > vc.LISTFILE_MAX and by_name[n]["id"] in oid
                 and oid[by_name[n]["id"]].lower() != n]
        if clash:
            new_clash = [c for c in clash if c[0].lower() in scope_l]
            if new_clash and strict:
                errors.append("%d custom ids also used by %s for other files: %s" % (len(new_clash), src, new_clash[:3]))
            else:
                warnings.append("%d custom ids shared with %s (pre-existing): %s" % (len(clash), src, clash[:3]))

    root = None
    try:
        root = vc.client_root()
    except Exception as ex:                                # no CASC reader: reference checks limited to the patches
        warnings.append("client CASC root unavailable (%s): references only checked against the patches" % ex)
    unresolved, bad_files, audits = [], [], {}
    for n in scope_names:
        p = os.path.join(patch_path, n)
        if not os.path.isfile(p):
            continue
        low = n.lower()
        e = by_name.get(low)
        if e and e["id"] > vc.LISTFILE_MAX and e["id"] not in prev_ids and root is not None and e["id"] in root:
            errors.append("new id %d (%s) is a file of the 9.2.7 client" % (e["id"], n))
        if low.endswith((".m2", ".skel")):
            raw = open(p, "rb").read()
            if low.endswith(".m2") and raw[:4] not in (b"MD21", b"MD20"):
                bad_files.append(n)
                continue
            for kind, fid, _ in vc.file_refs(raw):
                if fid not in in_patch and (root is None or fid not in root):
                    unresolved.append((n, kind, fid))
            if low.endswith(".m2") and os.path.exists(vc.AUDIT_LODS):
                bad, worst = audit_chain(patch_path, n, raw, by_id={e["id"]: e for e in ents})
                if bad:
                    audits[n] = worst
        elif low.endswith(".blp"):
            with open(p, "rb") as f:
                head = f.read(148)
            size = os.path.getsize(p)
            if head[:4] == b"BLP2" and len(head) >= 148:
                offs = struct.unpack_from("<16I", head, 20)
                lens = struct.unpack_from("<16I", head, 84)
                if offs[0] == 0 or any(o and o + l > size for o, l in zip(offs, lens)):
                    bad_files.append(n)
            elif head[:4] not in (b"BLP1", b"BLP2"):
                bad_files.append(n)
        elif low.endswith(".skin"):
            with open(p, "rb") as f:
                if f.read(4) != b"SKIN":
                    bad_files.append(n)
    if bad_files:
        (errors if strict else warnings).append("%d unreadable/corrupt files: %s" % (len(bad_files), bad_files[:5]))
    if unresolved:
        kinds = {}
        for _, k, _ in unresolved:
            kinds[k] = kinds.get(k, 0) + 1
        (errors if strict else warnings).append(
            "%d references resolve neither in the published patches nor in the client %s, e.g. %s"
            % (len(unresolved), kinds, unresolved[:4]))
    if audits:
        (errors if strict else warnings).append("%d LOD chains fail the spike/palette audit: %s"
                                                % (len(audits), list(audits)[:5]))
    save_hash_cache()
    return {"patch": patch_path, "entries": len(ents), "checked": len(scope_names), "errors": errors,
            "warnings": warnings, "unresolved_refs": unresolved[:200], "lod_audit_failures": audits,
            "ok": not errors}


def audit_chain(root, m, raw, by_id):
    sfid = ()
    for tag, off, size in vc.chunk_list(raw):
        if tag == b"SFID":
            sfid = struct.unpack_from("<%dI" % (size // 4), raw, off)
    skins = [by_id[i]["file"] for i in sfid if i in by_id]
    if sfid and sfid[0] not in by_id:
        base = os.path.splitext(m)[0] + "00.skin"
        skins = [base] + skins if os.path.exists(os.path.join(root, base)) else []
    if len(skins) < 2:
        return False, None
    res = subprocess.run([sys.executable, vc.AUDIT_LODS, os.path.join(root, m)] +
                         [os.path.join(root, s) for s in skins], capture_output=True, text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    try:
        data = json.loads(res.stdout[res.stdout.index("{"):])
    except ValueError:
        return True, "audit unreadable"
    worst, bad = {}, False
    for row in data.get("skins", {}).values():
        for k in ("index_out_of_range", "palette_errors", "bone_mismatch_vs_base", "tris_longer_than_base_max",
                  "longest_vs_base_max", "triangles"):
            worst[k] = max(worst.get(k, 0), row.get(k, 0) or 0)
        if row.get("index_out_of_range") or row.get("palette_errors") or row.get("bone_mismatch_vs_base"):
            bad = True
        if (row.get("longest_vs_base_max", 1) or 1) > 1.5 and \
                (row.get("tris_longer_than_base_max") or 0) > max(5, 0.01 * row.get("triangles", 0)):
            bad = True
    return bad, worst


# ---------------------------------------------------------------- published list
def add_published(path):
    """Validate and add a patch folder to the GitHub list; returns (ok, message)."""
    path = os.path.normpath(path)
    pub = vc.load_published()
    if not os.path.isfile(vc.path_in(path, "patch.json")):
        return False, "%s has no patch.json - pick an Epsilon patch folder" % path
    if _norm(path).startswith(_norm(vc.REPO)):
        return False, "that is inside the GitHub mirror - pick the patch folder in Patches"
    if any(_norm(p) == _norm(path) for p in pub):
        return False, "%s is already in the GitHub list - click Push to GitHub to upload it" % pname(path)
    if any(pname(p).lower() == pname(path).lower() for p in pub):
        return False, "another published patch is already called %s (they would install into the same folder)" % pname(path)
    coll = published_collisions(path, pub)
    if coll:
        return False, ("%d of its ids are already used by published patches, e.g. id %d: %s here vs %s in %s.\n"
                       "Use 'Merge into' instead - it renumbers clashing ids." % ((len(coll),) + coll[0][:2] + coll[0][3:4] + coll[0][2:3]))
    pub.append(path)
    vc.save_published(pub)
    return True, "%s added - it is uploaded with the next Push to GitHub" % pname(path)


def remove_published(name):
    pub = vc.load_published()
    keep = [p for p in pub if pname(p).lower() != name.lower()]
    if len(keep) == len(pub):
        return False, "%s is not on GitHub" % name
    vc.save_published(keep or [vc.MAIN])
    return True, "%s removed from the GitHub list - its files leave GitHub with the next push" % name


def patch_summary(path):
    try:
        p = vc.Patch(path)
    except (OSError, ValueError) as ex:
        return "%s  (unreadable: %s)" % (pname(path), ex)
    size = 0
    for e in p.entries:
        try:
            size += os.path.getsize(os.path.join(path, e["file"]))
        except OSError:
            pass
    return "%s  -  %d files, %.2f GB  -  %s" % (pname(path), len(p.entries), size / 1e9, path)


# ---------------------------------------------------------------- publish
GIT_ENV = dict(os.environ, GIT_TERMINAL_PROMPT="0")     # never wait on a console prompt; GCM shows its own window


def git(*args, check_rc=True, input=None):
    # CREATE_NO_WINDOW: under the GUI (pyw) every git call otherwise opens an empty console window
    r = subprocess.run(["git", "-c", "safe.directory=" + os.path.abspath(vc.REPO), "-C", vc.REPO, "-c", "http.sslBackend=schannel"] + list(args), capture_output=True,
                       text=True, input=input, encoding="utf-8", errors="replace", env=GIT_ENV,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if check_rc and r.returncode:
        err = r.stdout + r.stderr
        if "could not read Username" in err or "Authentication failed" in err or "terminal prompts disabled" in err:
            err += ("\nGitHub sign-in did not complete. Click Push to GitHub again and finish the 'Connect to GitHub' "
                    "window (Sign in with your browser).")
        raise RuntimeError("git %s failed:\n%s" % (" ".join(args[:2]), err))
    return r.stdout


def github_status():
    """One line for the GUI: how much of the local mirror is not on GitHub yet (from the last fetch/push)."""
    has_remote = git("rev-parse", "--verify", "-q", "origin/main", check_rc=False).strip()
    todo = git("rev-list", "--count", "origin/main..HEAD" if has_remote else "HEAD", check_rc=False).strip()
    if not todo or todo == "0":
        return "GitHub: up to date (%s)" % has_remote[:7] if has_remote else "GitHub: nothing pushed yet"
    return "GitHub: %s commit(s) waiting - click Push to GitHub" % todo


def ensure_credential_helper():
    """This Git for Windows has no credential.helper set, so pushes fell back to a console prompt (none under the GUI).
    Use Git Credential Manager for this repository only: the first push opens the GitHub browser sign-in, later pushes
    reuse the stored login."""
    if not git("config", "--get-all", "credential.helper", check_rc=False).strip():
        git("config", "--local", "credential.helper", "manager")


def _mirror(src_path, dst_dir, version=None, previous=None):
    """Listed files + patch.json of one patch into dst_dir; returns its index entry."""
    p = vc.Patch(src_path)
    vc.no_links(dst_dir)
    os.makedirs(dst_dir, exist_ok=True)
    files = {}
    canonical_names = {n.casefold(): n for n in (previous or {}).get("files", {})}
    entries = [dict(e, file=canonical_names.get(e["file"].casefold(), e["file"])) for e in p.entries]
    for e in entries:
        n = e["file"]
        src, dst = vc.path_in(src_path, n), vc.path_in(dst_dir, n)
        sha = blob_sha(src)
        old = (previous or {}).get("files", {}).get(n, {})
        reusable = not os.path.exists(dst) and old.get("sha") == sha and old.get("size") == os.path.getsize(src)
        if not reusable and (not os.path.exists(dst) or os.path.getsize(dst) != os.path.getsize(src) or blob_sha(dst) != sha):
            shutil.copy2(src, _mk(dst))
        files[n] = {"id": e["id"], "size": os.path.getsize(src), "sha": sha}
    keep = {e["file"].lower() for e in p.entries} | {"patch.json"}
    for f in vc.list_files(dst_dir):
        if f.lower() not in keep:
            os.remove(vc.path_in(dst_dir, f))            # the local patch is the master; git history keeps the old copy
    pj = vc.path_in(dst_dir, "patch.json")
    if version:
        vc.write_patch_json(dst_dir, vv.metadata(p.meta, pname(src_path), version), entries)
    else:
        shutil.copy2(vc.path_in(src_path, "patch.json"), pj)
    rel = os.path.relpath(dst_dir, vc.REPO).replace("\\", "/")
    return {"repo_dir": rel, **({"version": version, "display_name": vv.display_name(pname(src_path), version), "source_manifest": vv.manifest_hash(p.meta)} if version else {}), "total_bytes": sum(v["size"] for v in files.values()),
            "patch_json": {"size": os.path.getsize(pj), "sha": vc.git_blob_sha(pj)}, "files": files}


def _prune_empty_dirs(root):
    for r, d, fs in os.walk(root, topdown=False):
        if r != root and not os.listdir(r):
            os.rmdir(r)


def sync_repo(published=None):
    """Mirror every published patch into <repo>/patches/<name> and write index.json (format 2)."""
    published = vc.load_published() if published is None else published
    previous = (published_index() or {}).get("patches", {})
    os.makedirs(vc.REPO_PATCHES, exist_ok=True)
    patches = {}
    destinations = set()
    for path in published:
        name = pname(path)
        sc = scan_folder(path)
        prior = previous.get(name)
        changed = sc["first"] or sc["new"] or sc["changed"] or sc["removed"] or sc["patch_json_changed"]
        version = vv.next_version(prior) if changed or not (prior or {}).get("version") else prior["version"]
        folder = vv.display_name(name, version).casefold()
        if folder in destinations:
            raise ValueError("Two patch names resolve to the same online folder.")
        destinations.add(folder)
        dst = vv.prepare(vc.REPO, name, prior, version)
        patches[name] = dict(prior or {}, **_mirror(path, dst, version, prior))
    for name, prior in previous.items():
        if name not in patches:
            dst = vc.path_in(vc.REPO, vv.repo_dir(name, prior))
            if os.path.isdir(dst):
                shutil.rmtree(dst)
    _prune_empty_dirs(vc.REPO_PATCHES)
    index = {"format": 2, "published": datetime.datetime.now().strftime("%Y-%m-%d %H:%M"), "patches": patches}
    try:
        with open(os.path.join(vc.REPO, "index.json"), encoding="utf-8") as source:
            old = json.load(source)
        if old.get("patches") == patches:
            index["published"] = old.get("published", index["published"])     # nothing changed: no new commit
    except (OSError, ValueError):
        pass
    with open(os.path.join(vc.REPO, "index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, indent=0, sort_keys=True)
    save_hash_cache()
    return index


def describe_changes(before, after):
    old = (before or {}).get("patches", {})
    parts = []
    for name, p in after["patches"].items():
        if name not in old:
            parts.append("+%s (%d files)" % (name, len(p["files"])))
            continue
        of, nf = old[name].get("files", {}), p["files"]
        new = sum(1 for k in nf if k not in of)
        chg = sum(1 for k in nf if k in of and of[k].get("sha") != nf[k]["sha"])
        rem = sum(1 for k in of if k not in nf)
        if new or chg or rem:
            parts.append("%s: %d new, %d changed, %d removed" % (name, new, chg, rem))
    parts += ["-%s" % n for n in old if n not in after["patches"]]
    return "; ".join(parts) or "no file changes"


def refresh_remote(say=log):
    """Number releases against current GitHub main without overwriting pending local publications."""
    say("reading current GitHub publication ...")
    git("fetch", "-q", "origin")
    if not git("rev-parse", "--verify", "-q", "origin/main", check_rc=False).strip():
        return
    behind, ahead = map(int, git("rev-list", "--left-right", "--count", "origin/main...HEAD").split())
    if behind and ahead:
        raise ValueError("GitHub changed while local publication commits are pending. Reconcile the mirror before publishing; no remote files were overwritten.")
    if behind:
        git("merge", "--ff-only", "origin/main")


def require_clean_stage():
    if git("diff", "--cached", "--name-only").strip():
        raise ValueError("There are already staged Git changes. Commit or unstage them before publishing patches.")


def publish(message=None, push=True, batch_bytes=400_000_000, skip_check=False, say=log):
    require_clean_stage()
    if push:
        refresh_remote(say)
    published = vc.load_published()
    gone = [p for p in published if not os.path.isfile(os.path.join(p, "patch.json"))]
    if gone:
        return {"ok": False, "errors": {pname(p): ["folder missing: %s - put it back or Remove it from GitHub" % p]
                                        for p in gone}}
    warnings = {}
    if not skip_check:
        errs = {}
        for p in published:
            say("checking %s ..." % pname(p))
            rep = check(p, "changed", published)
            if rep["errors"]:
                errs[pname(p)] = rep["errors"]
            if rep["warnings"]:
                warnings[pname(p)] = rep["warnings"]
        if errs:
            return {"ok": False, "errors": errs, "warnings": warnings}
    before = published_index()
    say("copying changed files into the GitHub mirror ...")
    index = sync_repo(published)
    msg = message or "Publish %s: %s" % (datetime.datetime.now().strftime("%Y-%m-%d"), describe_changes(before, index))
    status = git("status", "--porcelain", "-z", "--untracked-files=all")
    paths = []
    for item in status.split("\0"):
        if len(item) > 3:
            code, path = item[:2], item[3:]
            if "D" not in code and any(path.startswith(vv.repo_dir(n, p) + "/") for n, p in index["patches"].items()) and not path.endswith("/patch.json"):
                paths.append(path)
    # file batches first (each pushed on its own), patch.json files + index.json + deletions last: an updater only
    # ever sees an index whose files are all present at that commit
    batches, cur, size = [], [], 0
    for p in sorted(paths):
        s = os.path.getsize(os.path.join(vc.REPO, p))
        if cur and size + s > batch_bytes:
            batches.append(cur)
            cur, size = [], 0
        cur.append(p)
        size += s
    if cur:
        batches.append(cur)
    commits = 0
    for k, b in enumerate(batches[:-1] if len(batches) > 1 else []):
        git("add", "--sparse", "--pathspec-from-file=-", "--pathspec-file-nul", input="\0".join(b))
        git("commit", "-q", "-m", "%s (part %d/%d)" % (msg, k + 1, len(batches)))
        commits += 1
    names = list(index["patches"])
    vi.canonicalize(vc.REPO, names, index)
    scopes = sorted({vv.repo_dir(n, p) for snapshot in (index, before or {}) for n, p in snapshot.get("patches", {}).items()})
    vi.stage_release(vc.REPO, index, before)
    index = vi.finalize(vc.REPO, index, before)
    if git("diff", "--cached", "--name-only").strip():
        git("commit", "-q", "-m", msg + (" (part %d/%d)" % (len(batches), len(batches)) if len(batches) > 1 else ""))
        commits += 1
    rep = {"ok": True, "changes": msg, "commits": commits, "head": git("rev-parse", "HEAD", check_rc=False).strip(),
           "patches": list(index["patches"]), "check_warnings": warnings}
    if push:
        rep["pushed"] = push_pending(say)
    return rep


def push_pending(say=log):
    """Upload large object batches to a temporary ref; promote main only after the complete snapshot is valid."""
    import uuid
    clear_stale_lock(say)
    ensure_credential_helper()
    git("fetch", "-q", "origin", check_rc=False)
    has_remote = git("rev-parse", "--verify", "-q", "origin/main", check_rc=False).strip()
    todo = [c for c in git("rev-list", "--reverse", "origin/main..HEAD" if has_remote else "HEAD").split() if c]
    if not todo:
        return 0
    vi.validate_head(vc.REPO)
    if len(todo) == 1:
        say("publishing one verified snapshot ...")
        git("push", "-q", "origin", "%s:refs/heads/main" % todo[0])
    else:
        staging = "refs/heads/virgil-upload-" + uuid.uuid4().hex
        for k, commit in enumerate(todo):
            say("uploading batch %d/%d %s (published main stays unchanged) ..." % (k + 1, len(todo), commit[:7]))
            git("push", "-q", "origin", "%s:%s" % (commit, staging))
        # GitHub already has the objects. This one atomic ref update exposes the final index/assets together.
        say("promoting the complete verified release ...")
        git("push", "--atomic", "-q", "origin", "%s:refs/heads/main" % todo[-1], ":" + staging)
    return len(todo)


# ---------------------------------------------------------------- per-folder publishing (fast path)
def _working_index():
    """index.json as last COMMITTED (format 2). Each folder push adds only its own entry to it: the working-tree file
    can list a folder whose files are not all committed yet (an interrupted push), and publishing that listing would
    send everyone's updater after files GitHub doesn't have."""
    idx = published_index()
    if idx is None:
        try:
            idx = json.load(open(os.path.join(vc.REPO, "index.json"), encoding="utf-8"))
            idx["patches"] = {}
        except (OSError, ValueError):
            idx = {}
    idx = dict(idx)
    if "patches" not in idx:
        idx = {"patches": {idx["patch_folder"]: idx} if "patch_folder" in idx else {}}
    idx["format"] = 2
    return idx


def clear_stale_lock(say=log):
    """A git that was killed mid-command leaves .git/index.lock behind and every later git command refuses to run.
    Removed only when no git.exe is running."""
    lock = os.path.join(vc.REPO, ".git", "index.lock")
    if not os.path.exists(lock):
        return False
    r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq git.exe", "/NH"], capture_output=True, text=True,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if "git.exe" in r.stdout.lower():
        raise RuntimeError("another git is running on the mirror - wait for it to finish (or close the other "
                           "publisher window) and try again")
    os.remove(lock)
    say("cleared a stale git lock left by an interrupted run")
    return True


def scan_folder(path):
    """What changed in one patch folder since it was last pushed. Fast: a file is only re-hashed when its size or
    modified time differ from the hash cache, so an unchanged 6000-file patch scans in a few seconds."""
    p = vc.Patch(path)                                      # one directory walk: existence, size, time, link check
    name = pname(path)
    base_entry = (published_index() or {}).get("patches", {}).get(name)
    base = (base_entry or {}).get("files", {})
    canonical_names = {n.casefold(): n for n in base}
    files, new, changed, missing = {}, [], [], []
    root = os.path.abspath(path)
    for e in p.entries:
        n = canonical_names.get(e["file"].casefold(), e["file"])
        st = p.stat(n)
        if st is None:
            missing.append(n)
            continue
        sha = blob_sha(os.path.join(root, n.replace("/", os.sep)), st)
        files[n] = {"id": e["id"], "size": st[0], "sha": sha}
        if n not in base:
            new.append(n)
        elif base[n].get("sha") != sha or base[n].get("id") != e["id"]:
            changed.append(n)
    removed = [n for n in base if n not in files]
    pj_sha = blob_sha(os.path.join(root, "patch.json"), p.stat("patch.json"))
    save_hash_cache()
    return {"name": name, "path": path, "files": files, "new": new, "changed": changed, "removed": removed,
            "missing": missing, "first": base_entry is None, "patch_json_sha": pj_sha,
            "patch_json_changed": ((base_entry or {}).get("source_manifest") != vv.manifest_hash(p.meta) if (base_entry or {}).get("source_manifest") else (base_entry or {}).get("patch_json", {}).get("sha") != pj_sha),
            "bytes_to_send": sum(files[n]["size"] for n in new + changed)}


def scan_summary(sc):
    if sc["first"]:
        return "not on GitHub yet - %d files, %.2f GB" % (len(sc["files"]), sc["bytes_to_send"] / 1e9)
    n = len(sc["new"]) + len(sc["changed"]) + len(sc["removed"])
    if not n and not sc["patch_json_changed"]:
        return "up to date"
    return "%d new, %d changed, %d removed (%.1f MB to send)" % (len(sc["new"]), len(sc["changed"]),
                                                               len(sc["removed"]), sc["bytes_to_send"] / 1e6)


def quick_check(sc, published, deep=False):
    """Checks that matter for a push, limited to what changed: patch.json sane, files present and readable, no custom
    id fighting another published patch. deep=True adds the slow model checks (references + LOD audit) on the
    changed models only."""
    errors, warnings = [], []
    path = sc["path"]
    p = vc.Patch(path)
    ids = [e["id"] for e in p.entries]
    names = [e["file"].lower() for e in p.entries]
    if len(ids) != len(set(ids)):
        errors.append("duplicate ids in patch.json")
    if len(names) != len(set(names)):
        errors.append("duplicate file names in patch.json")
    if sc["missing"]:
        errors.append("%d files listed in patch.json are missing, e.g. %s" % (len(sc["missing"]), sc["missing"][:5]))
    bad = []
    for n in sc["new"] + sc["changed"]:
        low = n.lower()
        with open(vc.path_in(path, n), "rb") as f:
            head = f.read(148)
        size = os.path.getsize(vc.path_in(path, n))
        if low.endswith(".m2") and head[:4] not in (b"MD21", b"MD20"):
            bad.append(n)
        elif low.endswith(".skin") and head[:4] != b"SKIN":
            bad.append(n)
        elif low.endswith(".blp"):
            if head[:4] == b"BLP2" and len(head) >= 148:
                offs = struct.unpack_from("<16I", head, 20)
                lens = struct.unpack_from("<16I", head, 84)
                if offs[0] == 0 or any(o and o + l > size for o, l in zip(offs, lens)):
                    (warnings if sc["first"] else errors).append("BLP mip table past the file end: " + n)
            elif head[:4] not in (b"BLP1", b"BLP2"):
                bad.append(n)
    if bad:
        errors.append("%d unreadable files, e.g. %s" % (len(bad), bad[:5]))
    # custom ids another published patch uses for a different file (from the committed index - no file reading)
    mine = {e["id"]: e["file"] for e in p.entries}
    idx = published_index() or {}
    others = {}
    for nm, ent in idx.get("patches", {}).items():
        if nm != sc["name"] and not vc.exclusive_alternatives(sc["name"], nm):
            for f, info in ent.get("files", {}).items():
                others.setdefault(info["id"], []).append((nm, f))
    for other in published:                             # published but not pushed yet
        if pname(other) != sc["name"] and not vc.exclusive_alternatives(sc["name"], other) and pname(other) not in idx.get("patches", {}) and \
                os.path.isfile(os.path.join(other, "patch.json")):
            for i, f in patch_ids(other).items():
                others.setdefault(i, []).append((pname(other), f))
    clash = [(i, mine[i], o) for i in mine if i > vc.LISTFILE_MAX and i in others
             for o in others[i] if o[1].lower() != mine[i].lower()]
    if clash:
        errors.append("%d custom ids are used by another published patch for a different file, e.g. id %d: %s here, "
                      "%s in %s - use 'Merge into a patch' instead (it renumbers)" %
                      (len(clash), clash[0][0], clash[0][1], clash[0][2][1], clash[0][2][0]))
    shared = sum(1 for i in mine if i in others) - len(clash)
    if shared:
        warnings.append("%d game files are also overridden by another published patch (allowed)" % shared)
    if deep and (sc["new"] or sc["changed"]):
        rep = check(path, scope=sc["new"] + sc["changed"], published=published)
        errors += rep["errors"]
        warnings += rep["warnings"]
    return {"errors": errors, "warnings": warnings, "ok": not errors}


def push_folder(path, deep=False, say=log, push=True):
    """Push ONE folder: copy only its new/changed files into the mirror, drop its removed ones, update its entry in
    index.json, commit (~400 MB batches, patch.json + index.json last) and push. Other folders are not touched."""
    require_clean_stage()
    if push:
        refresh_remote(say)
    published = vc.load_published()
    if not any(_norm(p) == _norm(path) for p in published):
        return {"ok": False, "errors": ["%s is not in the GitHub list" % pname(path)]}
    clear_stale_lock(say)
    say("scanning %s ..." % pname(path))
    sc = scan_folder(path)
    say("  " + scan_summary(sc))
    todo = sc["new"] + sc["changed"] + sc["removed"]
    if not todo and not sc["patch_json_changed"] and not sc["first"]:
        pushed = push_pending(say) if push else 0
        return {"ok": True, "changes": "nothing new in %s" % sc["name"], "commits": 0, "pushed": pushed}
    say("checking%s ..." % (" (deep)" if deep else ""))
    qc = quick_check(sc, published, deep)
    for w in qc["warnings"]:
        say("  note: " + w)
    if not qc["ok"]:
        return {"ok": False, "errors": qc["errors"]}
    name = sc["name"]
    prior = (published_index() or {}).get("patches", {}).get(name)
    version = vv.next_version(prior)
    dst = vv.prepare(vc.REPO, name, prior, version)
    say("copying %d files into the GitHub mirror ..." % (len(sc["new"]) + len(sc["changed"])))
    for n in sc["new"] + sc["changed"]:
        d = vc.path_in(dst, n)
        if os.path.isfile(d) and os.path.getsize(d) == sc["files"][n]["size"] and blob_sha(d) == sc["files"][n]["sha"]:
            continue                                   # already mirrored by an earlier, interrupted push
        shutil.copy2(vc.path_in(path, n), _mk(d))
    keep = {n.lower() for n in sc["files"]} | {"patch.json"}
    if os.path.isdir(dst):
        for f in vc.list_files(dst):
            if f.lower() not in keep:
                os.remove(vc.path_in(dst, f))
        _prune_empty_dirs(dst)
    source_patch = vc.Patch(path)
    os.makedirs(dst, exist_ok=True)
    vc.write_patch_json(dst, vv.metadata(source_patch.meta, name, version), source_patch.entries)
    pj = vc.path_in(dst, "patch.json")
    idx = _working_index()
    idx["patches"][name] = {"repo_dir": "patches/" + vv.display_name(name, version), "version": version, "display_name": vv.display_name(name, version), "source_manifest": vv.manifest_hash(source_patch.meta), "total_bytes": sum(v["size"] for v in sc["files"].values()),
                            "patch_json": {"size": os.path.getsize(pj), "sha": vc.git_blob_sha(pj)},
                            "files": sc["files"]}
    idx["published"] = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
    with open(os.path.join(vc.REPO, "index.json"), "w", encoding="utf-8") as f:
        json.dump(idx, f, indent=0, sort_keys=True)
    msg = ("Add %s (%d files)" % (name, len(sc["files"])) if sc["first"] else
           "Update %s: %d new, %d changed, %d removed" % (name, len(sc["new"]), len(sc["changed"]), len(sc["removed"])))
    rel = vv.repo_dir(name, idx["patches"][name])
    paths = sorted(rel + "/" + n for n in sc["new"] + sc["changed"])
    batches, cur, size = [], [], 0
    for p_ in paths:
        s = os.path.getsize(vc.path_in(vc.REPO, p_))
        if cur and size + s > 400_000_000:
            batches.append(cur)
            cur, size = [], 0
        cur.append(p_)
        size += s
    if cur:
        batches.append(cur)
    commits = 0
    for k, b in enumerate(batches[:-1] if len(batches) > 1 else []):
        git("add", "--sparse", "--pathspec-from-file=-", "--pathspec-file-nul", input="\0".join(b))
        if git("diff", "--cached", "--name-only").strip():    # parts an interrupted push already committed are skipped
            git("commit", "-q", "-m", "%s (part %d/%d)" % (msg, k + 1, len(batches)))
            commits += 1
    vi.canonicalize(vc.REPO, [name], idx)
    vi.stage_release(vc.REPO, idx, published_index(), [name])
    idx = vi.finalize(vc.REPO, idx, published_index())
    if git("diff", "--cached", "--name-only").strip():
        git("commit", "-q", "-m", msg + (" (part %d/%d)" % (len(batches), len(batches)) if len(batches) > 1 else ""))
        commits += 1
    rep = {"ok": True, "changes": msg, "commits": commits, "warnings": qc["warnings"]}
    if push:
        rep["pushed"] = push_pending(say)
    return rep


def remove_folder(name, say=log, push=True):
    """Stop publishing a folder: its files leave the current GitHub version (they stay in the history) and everyone's
    updater removes it on their next update. The local folder is not touched."""
    require_clean_stage()
    if push:
        refresh_remote(say)
    vc.safe_relative(name, folder=True)
    clear_stale_lock(say)
    ok, msg = remove_published(name)
    if not ok:
        return {"ok": False, "errors": [msg]}
    prior = (_working_index().get("patches", {})).get(name, {})
    rel = vv.repo_dir(name, prior)
    dst = vc.path_in(vc.REPO, rel)
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    idx = _working_index()
    idx["patches"].pop(name, None)
    idx["published"] = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
    with open(os.path.join(vc.REPO, "index.json"), "w", encoding="utf-8") as f:
        json.dump(idx, f, indent=0, sort_keys=True)
    vi.stage_release(vc.REPO, idx, published_index(), [name])
    idx = vi.finalize(vc.REPO, idx, published_index())
    commits = 0
    if git("diff", "--cached", "--name-only").strip():
        git("commit", "-q", "-m", "Remove %s" % name)
        commits = 1
    rep = {"ok": True, "changes": "removed %s" % name, "commits": commits}
    if push:
        rep["pushed"] = push_pending(say)
    return rep


# ---------------------------------------------------------------- GUI
LAUNCHER_ART = r"D:\D-Fix\VirgilPatch\updater\assets\lordaeron-background.png"
THEME = {"bg": "#15110e", "panel": "#1c1814", "panel2": "#110f0d", "edge": "#a1773d", "edge_dim": "#4d3f2c",
         "gold": "#dcb470", "cream": "#f8e0b1", "text": "#e8e0cf", "muted": "#aba495", "primary": "#9d6425",
         "primary_hi": "#b47a35", "button": "#2a241b", "button_hi": "#3a3226", "sel": "#5e3315"}


def gui():
    """Chronicles of Lordaeron styled publisher window (matches the 1.4.x launcher). Resizable; the header art follows
    the window width. Every action below runs the same publishing functions as before."""
    import threading, queue, tkinter as tk
    from tkinter import ttk, filedialog, messagebox
    try:                                              # crisp text on scaled displays (Tk picks up the DPI)
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass
    T = THEME
    q = queue.Queue()
    root = tk.Tk()
    root.title("Chronicles of Lordaeron - Patch Publisher")
    root.configure(bg=T["bg"])
    sc_ = root.winfo_fpixels("1i") / 96.0
    root.geometry("%dx%d" % (1040 * sc_, 760 * sc_))
    root.minsize(int(820 * sc_), int(600 * sc_))
    try:
        root.iconbitmap(os.path.join(vc.WORK, "launcher", "virgil.ico"))
    except Exception:
        pass
    try:                                              # Windows 10/11 dark title bar
        import ctypes
        root.update_idletasks()
        hwnd = ctypes.windll.user32.GetParent(root.winfo_id())
        ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(ctypes.c_int(1)), 4)
    except Exception:
        pass
    busy = {"on": False}
    deep = tk.BooleanVar(value=False)
    retire = tk.BooleanVar(value=True)
    scans = {}

    # ---- styles
    st = ttk.Style(root)
    st.theme_use("clam")
    base = ("Segoe UI", 10)
    st.configure(".", background=T["bg"], foreground=T["text"], font=base, bordercolor=T["edge_dim"],
                 lightcolor=T["panel"], darkcolor=T["panel"], troughcolor=T["panel2"], focuscolor=T["edge"])
    st.configure("TFrame", background=T["bg"])
    st.configure("Panel.TFrame", background=T["panel"])
    st.configure("TLabel", background=T["bg"], foreground=T["text"])
    st.configure("Panel.TLabel", background=T["panel"], foreground=T["text"])
    st.configure("Muted.TLabel", background=T["panel"], foreground=T["muted"], font=("Segoe UI", 9))
    st.configure("Kicker.TLabel", background=T["bg"], foreground=T["gold"], font=("Segoe UI", 9, "bold"))
    st.configure("Status.TLabel", background=T["bg"], foreground=T["muted"], font=("Segoe UI", 9))
    st.configure("TButton", background=T["button"], foreground=T["text"], bordercolor=T["edge"], padding=(14, 7),
                 focusthickness=1, relief="flat")
    st.map("TButton", background=[("disabled", "#241f18"), ("pressed", T["panel2"]), ("active", T["button_hi"])],
           foreground=[("disabled", "#857661")])
    st.configure("Primary.TButton", background=T["primary"], foreground="#fbdda2", font=("Segoe UI", 10, "bold"),
                 bordercolor="#c08a45", padding=(18, 8))
    st.map("Primary.TButton", background=[("disabled", "#392f22"), ("pressed", "#7a4d1c"), ("active", T["primary_hi"])],
           foreground=[("disabled", "#857661")])
    st.configure("TCheckbutton", background=T["panel"], foreground=T["text"], indicatorbackground=T["panel2"],
                 indicatorforeground=T["gold"])
    st.map("TCheckbutton", background=[("active", T["panel"])], indicatorbackground=[("selected", T["panel2"])])
    st.configure("TNotebook", background=T["bg"], borderwidth=0, tabmargins=(0, 0, 0, 0))
    st.configure("TNotebook.Tab", background=T["bg"], foreground=T["muted"], padding=(16, 8), borderwidth=0,
                 font=("Segoe UI", 10))
    st.map("TNotebook.Tab", background=[("selected", T["panel"]), ("active", T["button"])],
           foreground=[("selected", T["cream"]), ("active", T["text"])], lightcolor=[("selected", T["edge"])])
    st.configure("Treeview", background=T["panel2"], fieldbackground=T["panel2"], foreground=T["text"],
                 rowheight=int(28 * sc_), bordercolor=T["edge_dim"], font=base)
    st.map("Treeview", background=[("selected", T["sel"])], foreground=[("selected", T["cream"])])
    st.configure("Treeview.Heading", background=T["panel"], foreground=T["gold"], font=("Segoe UI", 9, "bold"),
                 bordercolor=T["edge_dim"], relief="flat", padding=(8, 6))
    st.map("Treeview.Heading", background=[("active", T["button"])])
    st.configure("TCombobox", fieldbackground=T["panel2"], background=T["button"], foreground=T["text"],
                 arrowcolor=T["gold"], bordercolor=T["edge_dim"])
    st.map("TCombobox", fieldbackground=[("readonly", T["panel2"])], foreground=[("readonly", T["text"])],
           selectbackground=[("readonly", T["panel2"])], selectforeground=[("readonly", T["cream"])])
    root.option_add("*TCombobox*Listbox.background", T["panel2"])
    root.option_add("*TCombobox*Listbox.foreground", T["text"])
    root.option_add("*TCombobox*Listbox.selectBackground", T["sel"])
    st.configure("Gold.Vertical.TScrollbar", background=T["button"], troughcolor=T["panel2"], bordercolor=T["panel2"],
                 arrowcolor=T["gold"], lightcolor=T["button"], darkcolor=T["button"], gripcount=0)
    st.map("Gold.Vertical.TScrollbar", background=[("active", T["button_hi"])])
    st.configure("Gold.Horizontal.TProgressbar", troughcolor=T["panel2"], background="#dda74f", bordercolor=T["bg"],
                 lightcolor="#dda74f", darkcolor="#dda74f")

    # ---- header: the launcher's Lordaeron art, darkened, with the title over it
    header = tk.Canvas(root, height=int(150 * sc_), bg=T["bg"], highlightthickness=0, bd=0)
    header.pack(fill="x")
    art = {"src": None, "img": None, "w": 0}
    try:
        from PIL import Image, ImageTk, ImageDraw
        art["src"] = Image.open(LAUNCHER_ART).convert("RGB")
    except Exception:
        Image = None

    def draw_header(event=None):
        w, h = max(header.winfo_width(), 200), int(150 * sc_)
        header.delete("all")
        if art["src"] is not None and Image is not None and w != art["w"]:
            src = art["src"]
            crop_h = int(src.width * h / w)
            top = int(src.height * 0.18)
            im = src.crop((0, top, src.width, min(src.height, top + crop_h))).resize((w, h), Image.LANCZOS)
            shade = Image.new("RGBA", (w, h))
            d = ImageDraw.Draw(shade)
            for x in range(0, w, 4):                     # dark on the left (title), lighter towards the right
                d.rectangle((x, 0, x + 4, h), fill=(10, 9, 8, int(215 - 160 * x / w)))
            im = Image.alpha_composite(im.convert("RGBA"), shade)
            fade = Image.new("RGBA", (w, h))
            d = ImageDraw.Draw(fade)
            for y in range(h):
                d.line((0, y, w, y), fill=(21, 17, 14, int(200 * max(0, y - h * 0.55) / (h * 0.45))))
            art["img"] = ImageTk.PhotoImage(Image.alpha_composite(im, fade).convert("RGB"))
            art["w"] = w
        if art["img"] is not None:
            header.create_image(0, 0, image=art["img"], anchor="nw")
        x = int(28 * sc_)
        header.create_text(x, int(26 * sc_), text="P A T C H   P U B L I S H E R", fill=T["gold"],
                           font=("Segoe UI", 9, "bold"), anchor="w")
        header.create_text(x - 2, int(66 * sc_), text="CHRONICLES", fill=T["cream"], font=("Georgia", 28), anchor="w")
        header.create_text(x, int(106 * sc_), text="OF LORDAERON", fill=T["gold"], font=("Georgia", 18), anchor="w")
        header.create_line(0, h - 1, w, h - 1, fill=T["edge_dim"])
        header.create_text(w - x, int(26 * sc_), text=gh_text["v"], fill=T["muted"], font=("Segoe UI", 9), anchor="e")
        header.create_text(w - x, int(48 * sc_), text="EPSILON PHASE ID 35602", fill=T["gold"],
                           font=("Segoe UI", 8, "bold"), anchor="e")
    gh_text = {"v": ""}
    header.bind("<Configure>", draw_header)

    # ---- activity: status line, progress, log (packed before the tabs so it always keeps its space)
    act = ttk.Frame(root)
    act.pack(side="bottom", fill="both", expand=True, padx=int(20 * sc_), pady=(int(10 * sc_), int(14 * sc_)))
    nb = ttk.Notebook(root)                           # tabs keep their full height; the log takes what is left
    nb.pack(side="top", fill="x", padx=int(20 * sc_), pady=(int(14 * sc_), 0))
    t_upd, t_new, t_merge, t_rm = (ttk.Frame(nb, padding=int(16 * sc_), style="Panel.TFrame") for _ in range(4))
    nb.add(t_upd, text="Update folder")
    nb.add(t_new, text="New folder to push")
    nb.add(t_merge, text="Merge into a patch")
    nb.add(t_rm, text="Remove from GitHub")
    top_row = ttk.Frame(act)
    top_row.pack(fill="x")
    ttk.Label(top_row, text="ACTIVITY", style="Kicker.TLabel").pack(side="left")
    status = ttk.Label(top_row, text="Ready.", style="Status.TLabel")
    status.pack(side="left", padx=(int(14 * sc_), 0))
    btn_resume = ttk.Button(top_row, text="Push waiting commits")
    btn_resume.pack(side="right")
    bar = ttk.Progressbar(act, mode="determinate", value=0, style="Gold.Horizontal.TProgressbar")
    bar.pack(fill="x", pady=(int(8 * sc_), int(6 * sc_)))
    logf = tk.Frame(act, bg=T["panel2"], highlightthickness=1, highlightbackground=T["edge_dim"])
    logf.pack(fill="both", expand=True)
    out = tk.Text(logf, font=("Consolas", 9), height=5, bg=T["panel2"], fg="#d3c9b2", insertbackground=T["gold"],
                  relief="flat", bd=0, highlightthickness=0, padx=10, pady=8, selectbackground=T["sel"], wrap="word")
    sb = ttk.Scrollbar(logf, orient="vertical", command=out.yview, style="Gold.Vertical.TScrollbar")
    out.configure(yscrollcommand=sb.set)
    sb.pack(side="right", fill="y")
    out.pack(side="left", fill="both", expand=True)
    out.tag_configure("err", foreground="#e88b6a")
    out.tag_configure("ok", foreground=T["gold"])
    buttons = []

    def say(t):
        q.put(str(t))

    def pump():
        while not q.empty():
            line = q.get()
            tag = "err" if line.startswith("ERROR") else "ok" if (" pushed" in line or "up to date" in line or
                                                                   line.startswith("Scan finished")) else None
            out.insert("end", line + "\n", tag)
            out.see("end")
        root.after(150, pump)

    def set_busy(on, text=None):
        busy["on"] = on
        for b in buttons:
            b.state(["disabled"] if on else ["!disabled"])
        if on:
            bar.configure(mode="indeterminate")
            bar.start(12)
        else:
            bar.stop()
            bar.configure(mode="determinate", value=0)
        if text:
            status.config(text=text)

    def run(fn, text="Working..."):
        if busy["on"]:
            say("busy - wait for the current job to finish")
            return
        set_busy(True, text)

        def wrap():
            try:
                fn()
            except Exception as ex:
                say("ERROR: %s" % ex)
            finally:
                root.after(0, lambda: (set_busy(False, "Ready."), refresh_lists()))
        threading.Thread(target=wrap, daemon=True).start()

    def ask_main(kind, *a):
        r, ev = {}, threading.Event()

        def f():
            r["v"] = getattr(messagebox, kind)(*a)
            ev.set()
        root.after(0, f)
        ev.wait()
        return r["v"]

    def show(rep, ok_line):
        if rep.get("ok"):
            say("%s - %s" % (ok_line, rep.get("changes", "")))
            if rep.get("pushed") is not None:
                say("pushed %d commit(s) to GitHub" % rep["pushed"])
        else:
            for e in rep.get("errors", []):
                say("ERROR: " + e)

    def heading(parent, text, sub):
        ttk.Label(parent, text=text, style="Panel.TLabel", font=("Georgia", 15), foreground=T["cream"]).pack(anchor="w")
        ttk.Label(parent, text=sub, style="Muted.TLabel", wraplength=int(900 * sc_), justify="left").pack(anchor="w", pady=(2, 10))

    def folder_table(parent):
        frame = ttk.Frame(parent, style="Panel.TFrame")
        tv = ttk.Treeview(frame, columns=("version", "files", "status", "scanned"), height=6, selectmode="browse")
        tv.heading("#0", text="FOLDER", anchor="w")
        tv.heading("version", text="VERSION", anchor="w")
        tv.column("version", width=int(100 * sc_), stretch=False)
        tv.heading("files", text="FILES", anchor="e")
        tv.heading("status", text="STATUS", anchor="w")
        tv.heading("scanned", text="SCANNED", anchor="e")
        tv.column("#0", width=int(250 * sc_), minwidth=int(160 * sc_), stretch=False)
        tv.column("files", width=int(80 * sc_), anchor="e", stretch=False)
        tv.column("status", width=int(300 * sc_), minwidth=int(140 * sc_), stretch=True)
        tv.column("scanned", width=int(90 * sc_), anchor="e", stretch=False)
        tv.tag_configure("changed", foreground=T["cream"])
        tv.tag_configure("same", foreground=T["muted"])
        tv.tag_configure("bad", foreground="#e88b6a")
        tv.pack(side="left", fill="both", expand=True)
        return frame, tv

    def sel_path(tv):
        pub = vc.load_published()
        s = tv.selection()
        names = [pname(p) for p in pub]
        return pub[names.index(s[0])] if s and s[0] in names else None

    # --- Update folder
    heading(t_upd, "Update a folder on GitHub", "Scan shows what changed in every published folder (seconds, not "
            "minutes). Pick one and Push sends only that folder's new and changed files.")
    row = ttk.Frame(t_upd, style="Panel.TFrame")
    row.pack(fill="x")
    side = ttk.Frame(row, style="Panel.TFrame")
    side.pack(side="right", padx=(int(14 * sc_), 0), anchor="n")
    tbl, tv_upd = folder_table(row)
    tbl.pack(side="left", fill="x", expand=True)
    ttk.Checkbutton(t_upd, text="Deep check (model references and LOD audit on changed models - slower)",
                    variable=deep).pack(anchor="w", pady=(10, 0))

    def scan_job():
        import time as _t
        t_all = _t.time()
        for p in vc.load_published():
            t0 = _t.time()
            if os.path.isfile(os.path.join(p, "patch.json")):
                sc = scan_folder(p)
                n = len(sc["new"]) + len(sc["changed"]) + len(sc["removed"]) + len(sc["missing"])
                scans[pname(p)] = (scan_summary(sc), len(sc["files"]), "%.1f s" % (_t.time() - t0),
                                   "bad" if sc["missing"] else "changed" if n or sc["first"] or sc["patch_json_changed"] else "same")
            else:
                scans[pname(p)] = ("folder missing: " + p, 0, "", "bad")
            say("%s: %s" % (pname(p), scans[pname(p)][0]))
            root.after(0, refresh_lists)
        save_hash_cache()
        say("Scan finished in %.1f s" % (_t.time() - t_all))

    def do_scan_all():
        run(scan_job, "Scanning published folders...")

    def do_push_sel():
        p = sel_path(tv_upd)
        if not p:
            say("pick a folder first")
            return

        def job():
            rep = push_folder(p, deep=deep.get(), say=say)
            if rep.get("ok"):
                sc = scan_folder(p)
                scans[pname(p)] = (scan_summary(sc), len(sc["files"]), "now", "same")
            show(rep, "%s pushed" % pname(p))
        run(job, "Pushing %s..." % pname(p))

    b1 = ttk.Button(side, text="Scan for changes", command=do_scan_all)
    b1.pack(fill="x")
    b2 = ttk.Button(side, text="Push changes", style="Primary.TButton", command=do_push_sel)
    b2.pack(fill="x", pady=(8, 0))
    buttons += [b1, b2]

    # --- New folder to push
    heading(t_new, "Put a new folder on GitHub", "Uploads a whole patch folder as its own folder (for example "
            "Chronicles-Northrend). Only this folder is uploaded; big folders go up in ~400 MB parts.")
    new_info = ttk.Label(t_new, text="No folder picked.", style="Muted.TLabel")
    new_info.pack(anchor="w", pady=(0, 10))
    new_pick = {"path": None}

    def do_pick_new():
        path = filedialog.askdirectory(initialdir=vc.PATCHES, title="Pick the patch folder to put on GitHub")
        if not path:
            return
        path = os.path.normpath(path)

        def job():
            if not os.path.isfile(vc.path_in(path, "patch.json")):
                say("ERROR: %s has no patch.json" % path)
                return
            pub = vc.load_published()
            if any(_norm(p) == _norm(path) for p in pub):
                say("%s is already on GitHub - use the Update folder tab" % pname(path))
                return
            say("scanning %s ..." % pname(path))
            pt = vc.Patch(path)
            coll = published_collisions(path, pub)
            size = sum((pt.stat(e["file"]) or (0, 0))[0] for e in pt.entries)
            txt = "%s  -  %d files, %.2f GB" % (pname(path), len(pt.entries), size / 1e9)
            if coll:
                txt += "  -  BLOCKED: %d custom ids clash with published patches" % len(coll)
                say("ERROR: %d custom ids are used by a published patch for a different file, e.g. id %d: %s here, "
                    "%s in %s. Use 'Merge into a patch' instead." % (len(coll), coll[0][0], coll[0][1], coll[0][3], coll[0][2]))
                new_pick["path"] = None
            else:
                new_pick["path"] = path
                say("%s is ready to push" % txt)
            root.after(0, lambda: new_info.config(text=txt))
        run(job, "Checking %s..." % pname(path))

    def do_push_new():
        path = new_pick["path"]
        if not path:
            say("pick a folder first")
            return
        if not messagebox.askyesno("New folder to push", "Upload %s to GitHub as its own folder?\n\nBig folders upload in "
                                   "~400 MB parts; if it stops, Push again carries on." % pname(path)):
            return

        def job():
            ok, msg = add_published(path)
            if not ok:
                say("ERROR: " + msg)
                return
            rep = push_folder(path, deep=False, say=say)
            show(rep, "%s pushed" % pname(path))
            new_pick["path"] = None
            root.after(0, lambda: new_info.config(text="No folder picked."))
        run(job, "Uploading %s..." % pname(path))

    nrow = ttk.Frame(t_new, style="Panel.TFrame")
    nrow.pack(anchor="w")
    b3 = ttk.Button(nrow, text="Pick folder...", command=do_pick_new)
    b3.pack(side="left")
    b4 = ttk.Button(nrow, text="Push this folder", style="Primary.TButton", command=do_push_new)
    b4.pack(side="left", padx=8)
    buttons += [b3, b4]

    # --- Merge into a patch
    heading(t_merge, "Merge new content into a published folder", "Adds an exported patch INTO a folder. Clashing "
            "custom ids are renumbered and references rewritten; everything is backed up and rolled back if the "
            "check fails.")
    mrow = ttk.Frame(t_merge, style="Panel.TFrame")
    mrow.pack(fill="x", pady=(0, 10))
    ttk.Label(mrow, text="Into", style="Panel.TLabel").pack(side="left")
    target = ttk.Combobox(mrow, state="readonly", width=48)
    target.pack(side="left", padx=8)
    mrow2 = ttk.Frame(t_merge, style="Panel.TFrame")
    mrow2.pack(fill="x")

    def target_path():
        pub = vc.load_published()
        names = [pname(p) for p in pub]
        return pub[names.index(target.get())] if target.get() in names else pub[0]

    def do_merge(path):
        if not path:
            return
        tgt = target_path()

        def job():
            if vc.wow_running() and not ask_main("askyesno", "WoW is running",
                                                 "Wow.exe is running and may hold patch files open. Continue anyway?"):
                return
            say("planning merge of %s into %s ..." % (path, pname(tgt)))
            plan = plan_merge(path, main_path=tgt)
            say(summarize(plan))
            override = "skip"
            conflicts = [a for a in plan["actions"] if a["kind"] == "override_conflict"]
            if conflicts:
                yes = ask_main("askyesnocancel", "Game-file overrides clash",
                               "%d incoming files override game files %s already overrides (e.g. %s vs %s).\n\n"
                               "Yes = use the incoming files\nNo = keep the target's files\nCancel = stop"
                               % (len(conflicts), pname(tgt), conflicts[0]["src"], conflicts[0]["main_file"]))
                if yes is None:
                    say("cancelled")
                    return
                override = "replace" if yes else "skip"
            if not ask_main("askyesno", "Merge", "Merge this into %s?\n(everything is backed up)" % pname(tgt)):
                say("cancelled")
                return
            rep = apply_merge(plan, override=override, retire=retire.get())
            say(json.dumps({k: v for k, v in rep.items() if k not in ("unresolved_refs",)}, indent=1)[:3000])
            say("nothing new to merge" if rep.get("nothing_to_merge") else "ROLLED BACK - nothing changed"
                if rep.get("rolled_back") else "merged OK - test in game, then push it from the Update folder tab")
        run(job, "Merging into %s..." % pname(tgt))

    def do_undo():
        if messagebox.askyesno("Undo", "Undo the most recent merge (restores the backed-up files and patch.json)?"):
            run(lambda: say("undone: %s" % undo()), "Undoing the last merge...")

    b5 = ttk.Button(mrow2, text="Merge patch folder...", style="Primary.TButton", command=lambda: do_merge(
        filedialog.askdirectory(initialdir=vc.PATCHES, title="Pick the patch whose content to merge in")))
    b5.pack(side="left")
    b6 = ttk.Button(mrow2, text="Merge .patch file...", command=lambda: do_merge(filedialog.askopenfilename(
        initialdir=vc.PATCHES, filetypes=[("Epsilon patch", "*.patch"), ("all", "*.*")])))
    b6.pack(side="left", padx=8)
    ttk.Checkbutton(mrow2, text="Move the merged source patch out of Patches", variable=retire).pack(side="left", padx=8)
    b7 = ttk.Button(mrow2, text="Undo last merge", command=do_undo)
    b7.pack(side="right")
    buttons += [b5, b6, b7]

    # --- Remove from GitHub
    heading(t_rm, "Take a folder off GitHub", "Your local folder is not touched and the old files stay in GitHub's "
            "history; everyone's updater removes the folder on their next update.")
    rrow = ttk.Frame(t_rm, style="Panel.TFrame")
    rrow.pack(fill="x")
    rm_side = ttk.Frame(rrow, style="Panel.TFrame")
    rm_side.pack(side="right", padx=(int(14 * sc_), 0), anchor="n")
    tbl2, tv_rm = folder_table(rrow)
    tbl2.pack(side="left", fill="x", expand=True)

    def do_remove():
        p = sel_path(tv_rm)
        if not p:
            say("pick a folder first")
            return
        if not messagebox.askyesno("Remove from GitHub", "Remove %s from GitHub and push that now?" % pname(p)):
            return
        run(lambda: show(remove_folder(pname(p), say=say), "%s removed" % pname(p)), "Removing %s..." % pname(p))

    b8 = ttk.Button(rm_side, text="Remove from GitHub", command=do_remove)
    b8.pack(fill="x")
    buttons.append(b8)

    # --- shared
    def refresh_lists():
        pub = vc.load_published()
        for tv in (tv_upd, tv_rm):
            keep = tv.selection()
            tv.delete(*tv.get_children())
            for p in pub:
                n = pname(p)
                info = scans.get(n, ("click Scan for changes", "", "", "same"))
                entry = (published_index() or {}).get("patches", {}).get(n, {})
                online = entry.get("version", "not versioned")
                label = entry.get("display_name", n)
                tv.insert("", "end", iid=n, text="  " + label, values=(online, info[1], info[0], info[2]), tags=(info[3],))
            names = [pname(p) for p in pub]
            if names:
                tv.selection_set(keep[0] if keep and keep[0] in names else names[0])
        names = [pname(p) for p in pub]
        target["values"] = names
        if target.get() not in names and names:
            target.set(names[0])
        try:
            gh_text["v"] = github_status()
        except Exception as ex:
            gh_text["v"] = "GitHub: %s" % ex
        draw_header()

    def do_resume():
        run(lambda: say("pushed %d waiting commit(s)" % push_pending(say)), "Pushing waiting commits...")

    btn_resume.config(command=do_resume)
    buttons.append(btn_resume)
    refresh_lists()
    pump()
    do_scan_all()
    root.mainloop()


def main(argv=None):
    if not argv:
        return gui()
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("list")
    sub.add_parser("scan")
    pf = sub.add_parser("push-folder")
    pf.add_argument("name")
    pf.add_argument("--deep", action="store_true")
    nf = sub.add_parser("new-folder")
    nf.add_argument("folder")
    rf = sub.add_parser("remove-folder")
    rf.add_argument("name")
    ad = sub.add_parser("add")
    ad.add_argument("folder")
    rm = sub.add_parser("remove")
    rm.add_argument("name")
    m = sub.add_parser("merge")
    m.add_argument("incoming")
    m.add_argument("--into", help="published patch name (default: the first, the Chronicles texture patch)")
    m.add_argument("--override", choices=["replace", "skip"], default="skip")
    m.add_argument("--retire", action="store_true")
    m.add_argument("--dry-run", action="store_true")
    c = sub.add_parser("check")
    c.add_argument("--all", action="store_true")
    p = sub.add_parser("publish")
    p.add_argument("--no-push", action="store_true")
    p.add_argument("-m", "--message")
    sub.add_parser("push")
    u = sub.add_parser("undo")
    u.add_argument("backup", nargs="?")
    a = ap.parse_args(argv)
    if a.cmd == "scan":
        for x in vc.load_published():
            log("%s: %s" % (pname(x), scan_summary(scan_folder(x))))
        save_hash_cache()
        return 0
    if a.cmd == "push-folder":
        x = next((x for x in vc.load_published() if pname(x).lower() == a.name.lower()), None)
        if x is None:
            log("no published folder called %s" % a.name)
            return 1
        rep = push_folder(x, deep=a.deep)
        log(json.dumps(rep, indent=1))
        return 0 if rep["ok"] else 1
    if a.cmd == "new-folder":
        ok, msg = add_published(a.folder)
        log(msg)
        if not ok:
            return 1
        rep = push_folder(os.path.normpath(a.folder))
        log(json.dumps(rep, indent=1))
        return 0 if rep["ok"] else 1
    if a.cmd == "remove-folder":
        rep = remove_folder(a.name)
        log(json.dumps(rep, indent=1))
        return 0 if rep["ok"] else 1
    if a.cmd == "list":
        for x in vc.load_published():
            log(patch_summary(x))
    elif a.cmd == "add":
        ok, msg = add_published(a.folder)
        log(msg)
        return 0 if ok else 1
    elif a.cmd == "remove":
        ok, msg = remove_published(a.name)
        log(msg)
        return 0 if ok else 1
    elif a.cmd == "merge":
        pub = vc.load_published()
        target = next((x for x in pub if pname(x).lower() == a.into.lower()), None) if a.into else pub[0]
        if target is None:
            log("no published patch called %s" % a.into)
            return 1
        plan = plan_merge(a.incoming, main_path=target)
        log(summarize(plan))
        if not a.dry_run:
            rep = apply_merge(plan, override=a.override, retire=a.retire)
            log(json.dumps({k: v for k, v in rep.items() if k != "unresolved_refs"}, indent=1))
            return 0 if rep["ok"] else 1
    elif a.cmd == "check":
        ok = True
        for x in vc.load_published():
            rep = check(x, "all" if a.all else "changed")
            log(json.dumps(rep, indent=1)[:20000])
            ok &= rep["ok"]
        return 0 if ok else 1
    elif a.cmd == "publish":
        rep = publish(a.message, push=not a.no_push)
        log(json.dumps(rep, indent=1)[:20000])
        return 0 if rep["ok"] else 1
    elif a.cmd == "push":
        log("pushed %d commits" % push_pending())
    elif a.cmd == "undo":
        log("undone:", undo(a.backup))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
