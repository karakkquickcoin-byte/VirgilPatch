"""Online release names are independent of permanent local installation keys."""
import hashlib, json, os, re
import virgil_core as vc
BASE = "5.0.0"
_PATTERN = r"(?:0|[1-9][0-9]{0,8})"

def valid(value):
    if not isinstance(value, str) or not re.fullmatch(_PATTERN + r"\." + _PATTERN + r"\." + _PATTERN + r"(?:\." + _PATTERN + r")?", value):
        raise ValueError("Invalid online patch version.")
    return value

def next_version(previous):
    if not previous or not previous.get("version"):
        return BASE
    parts = valid(previous["version"]).split(".")
    revision = int(parts[3]) if len(parts) == 4 else 0
    if revision >= 999999999:
        raise ValueError("Patch revision limit reached.")
    return ".".join(parts[:3] + [str(revision + 1)])

def display_name(key, version):
    vc.safe_relative(key, folder=True)
    stem = re.sub(r"\([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\)$", "", key).rstrip()
    return vc.safe_relative(stem + "(" + valid(version) + ")", folder=True)

def repo_dir(key, entry):
    vc.safe_relative(key, folder=True)
    expected = "patches/" + (display_name(key, entry["version"]) if "version" in entry else key)
    if entry.get("repo_dir", expected) != expected:
        raise ValueError("Unexpected repository path for patch: " + key)
    return expected

def manifest_hash(meta):
    value = {k:v for k,v in meta.items() if k not in ("name", "version")}
    value["files"] = sorted((dict(e, file=e["file"].lower()) for e in meta["files"]), key=lambda e:e["file"])
    return hashlib.sha256(json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode()).hexdigest()

def metadata(meta, key, version):
    return dict(meta, name=display_name(key, version), version=valid(version))

def prepare(repo, key, previous, version):
    """Move only the controlled Git mirror; never rename the source/install folder."""
    target = vc.path_in(repo, "patches/" + display_name(key, version))
    index_path = vc.path_in(repo, "index.json")
    if os.path.isfile(index_path):
        with open(index_path, encoding="utf-8") as source:
            entries = json.load(source).get("patches", {})
        for other, entry in entries.items():
            if other != key and os.path.normcase(vc.path_in(repo, repo_dir(other, entry))) == os.path.normcase(target):
                raise ValueError("Online version folder belongs to another patch.")
    old = vc.path_in(repo, repo_dir(key, previous)) if previous else None
    if old and os.path.normcase(old) != os.path.normcase(target) and os.path.isdir(old):
        if os.path.exists(target):
            raise ValueError("Version folder already exists; finish or repair the interrupted publish first: " + target)
        os.rename(old, target)
    return target

def verify_metadata(key, entry, meta):
    repo_dir(key, entry)
    if "version" in entry and (meta.get("version") != entry["version"] or meta.get("name") != display_name(key, entry["version"])):
        raise ValueError("Published patch name/version disagrees with index: " + key)
