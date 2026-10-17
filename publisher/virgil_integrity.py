"""Publish manifests from the actual Git snapshot, preserving case-sensitive raw download paths."""
import json
import os
import subprocess
import tempfile
import re


def strict_json(raw):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('Duplicate JSON property: ' + key)
            result[key] = value
        return result
    def invalid(value):
        raise ValueError('Non-JSON numeric constant: ' + value)
    if isinstance(raw, bytes):
        raw = raw.decode('utf-8-sig', errors='strict')
    return json.loads(raw, object_pairs_hook=unique, parse_constant=invalid)


def git_bytes(repo, *args, input=None):
    result = subprocess.run(['git', '-C', repo, *args], input=input, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if result.returncode:
        raise RuntimeError(result.stderr.decode('utf-8', 'replace'))
    return result.stdout


def records(repo, revision=None):
    data = git_bytes(repo, 'ls-tree', '-r', '-z', revision) if revision else git_bytes(repo, 'ls-files', '--stage', '-z')
    out = {}
    for row in data.split(b'\0'):
        if not row:
            continue
        header, name = row.split(b'\t', 1)
        mode, sha, stage = header.decode().split()
        if revision:
            mode, kind, sha = header.decode().split()
        elif stage != '0':
            raise ValueError('Cannot publish unresolved Git conflicts.')
        path = name.decode('utf-8')
        out[path] = {'sha': sha, 'mode': mode}
    return out


def write_json(path, value):
    import virgil_core as vc
    vc.no_links(path)
    folder = os.path.dirname(os.path.abspath(path))
    fd, temporary = tempfile.mkstemp(prefix='manifest-', suffix='.tmp', dir=folder)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8', newline='\n') as output:
            json.dump(value, output, separators=(',', ':'), ensure_ascii=False)
        vc.no_links(path)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


class ObjectSizes:
    """Git blob identity is immutable; cache exact Git-measured sizes by blob hash, never by file mtime."""
    def __init__(self, repo, seed=None):
        self.repo = repo
        directory = git_bytes(repo, 'rev-parse', '--absolute-git-dir').decode().strip()
        self.path = os.path.join(directory, 'virgil-object-sizes.json')
        self.values = {}
        self.dirty = False
        try:
            if os.path.getsize(self.path) < 32 * 1024 * 1024:
                import virgil_core as vc
                vc.no_links(self.path)
                with open(self.path, 'rb') as source:
                    self.values = {k: v for k, v in strict_json(source.read()).items()
                                   if re.fullmatch('[0-9a-f]{40}', k) and type(v) is int and v >= 0}
        except (OSError, ValueError):
            pass
        self.values.update(seed or {})
    def get(self, sha):
        if sha not in self.values:
            self.values[sha] = int(git_bytes(self.repo, 'cat-file', '-s', sha))
            self.dirty = True
        return self.values[sha]
    def prime(self, hashes):
        missing = sorted(set(hashes) - self.values.keys())
        if not missing:
            return
        # One Git process for a cold repository, rather than tens of thousands of subprocesses.
        raw = git_bytes(self.repo, 'cat-file', '--batch-check=%(objectname) %(objecttype) %(objectsize)',
                        input=('\n'.join(missing) + '\n').encode('ascii'))
        rows = raw.decode('ascii').splitlines()
        if len(rows) != len(missing):
            raise ValueError('Could not measure every published Git blob.')
        for wanted, row in zip(missing, rows):
            sha, kind, size = row.split()
            if sha != wanted or kind != 'blob':
                raise ValueError('Published file is not a Git blob: ' + wanted)
            self.values[sha] = int(size)
        self.dirty = True
    def save(self):
        if self.dirty:
            write_json(self.path, self.values)


def canonicalize(repo, names):
    import virgil_core as vc
    tracked = records(repo)
    folded = {path.casefold(): path for path in tracked}
    if len(folded) != len(tracked):
        raise ValueError('Git contains case-colliding paths that cannot be installed on Windows.')
    for name in names:
        vc.safe_relative(name, folder=True)
        root = vc.path_in(repo, 'patches/' + name)
        patch = vc.Patch(root)
        with open(vc.path_in(root, 'patch.json'), 'rb') as source:
            strict_json(source.read())
        entries = []
        ids = set()
        for entry in patch.entries:
            if entry['id'] in ids:
                raise ValueError('Duplicate FileDataID in ' + name)
            ids.add(entry['id'])
            path = 'patches/' + name + '/' + entry['file']
            actual = folded.get(path.casefold())
            if actual:
                filename = actual[len('patches/' + name + '/'):]
            else:
                match = patch.tree.get(entry['file'].lower())
                if not match:
                    raise ValueError('Missing listed file: ' + path)
                filename = match[0]
            vc.safe_relative(filename, asset=True)
            entries.append(dict(entry, file=filename))
        meta = dict(patch.meta)
        if not isinstance(meta.get('name'), str) or not meta['name'].strip():
            meta['name'] = name
        if not isinstance(meta.get('version'), str) or not meta['version'].strip():
            meta['version'] = '1'
        canonical = dict(meta, files=entries)
        if canonical != patch.meta:
            vc.write_patch_json(root, meta, entries)


def finalize(repo, index, baseline=None, verified_sizes=None):
    """Use staged blobs, not caches/working files, for the release snapshot. No asset bytes are modified."""
    import virgil_core as vc
    objects = records(repo)
    folded = {p.casefold(): p for p in objects}
    if len(folded) != len(objects):
        raise ValueError('Case-colliding Git paths.')
    result = dict(index)
    result['patches'] = {}
    measured = ObjectSizes(repo, verified_sizes)
    measured.prime(value['sha'] for path, value in objects.items() if path.startswith('patches/'))
    def size(sha):
        return measured.get(sha)
    for name, previous in index['patches'].items():
        vc.safe_relative(name, folder=True)
        prefix = 'patches/' + name
        metadata_path = prefix + '/patch.json'
        if metadata_path not in objects:
            raise ValueError('Missing staged patch.json: ' + name)
        metadata = strict_json(git_bytes(repo, 'show', ':' + metadata_path))
        vc.validate_entries(metadata.get('files'))
        if not isinstance(metadata.get('name'), str) or not metadata['name'].strip():
            raise ValueError('Missing patch name: ' + name)
        files, ids = {}, set()
        old = {k.casefold(): v for k, v in previous.get('files', {}).items()}
        for entry in metadata['files']:
            path = prefix + '/' + entry['file']
            if path not in objects or objects[path]['mode'] not in ('100644', '100755'):
                raise ValueError('Listed path does not exist with exact Git casing: ' + path)
            if entry['id'] in ids:
                raise ValueError('Duplicate FileDataID: ' + name)
            ids.add(entry['id'])
            sha = objects[path]['sha']
            before = old.get(entry['file'].casefold(), {})
            length = size(sha)
            files[entry['file']] = {'id': entry['id'], 'sha': sha, 'size': length}
        meta_sha = objects[metadata_path]['sha']
        result['patches'][name] = dict(previous, repo_dir=prefix, files=files,
                                      total_bytes=sum(v['size'] for v in files.values()),
                                      patch_json={'size': size(meta_sha), 'sha': meta_sha})
        prior = (baseline or index).get('patches', {}).get(name, {})
        old_files = {k.casefold(): v for k, v in prior.get('files', {}).items()}
        new_names = {k.casefold() for k in files}
        added = [k for k in files if k.casefold() not in old_files]
        changed = [k for k, v in files.items() if k.casefold() in old_files and old_files[k.casefold()] != v]
        removed = [k for k in prior.get('files', {}) if k.casefold() not in new_names]
        if added or changed or removed or prior.get('patch_json') != result['patches'][name]['patch_json']:
            result['patches'][name]['changes'] = {'published': index.get('published', ''),
                'added_count': len(added), 'changed_count': len(changed), 'removed_count': len(removed),
                'added': sorted(added)[:100], 'changed': sorted(changed)[:100], 'removed': sorted(removed)[:100]}
    write_json(vc.path_in(repo, 'index.json'), result)
    git_bytes(repo, 'add', '--', 'index.json')
    measured.save()
    return result


def validate_head(repo):
    """Refuse to promote any incomplete snapshot, including unrelated manifests left by an interrupted publish."""
    import virgil_core as vc
    objects = records(repo, 'HEAD')
    measured = ObjectSizes(repo)
    measured.prime(value['sha'] for path, value in objects.items() if path.startswith('patches/'))
    index = strict_json(git_bytes(repo, 'show', 'HEAD:index.json'))
    errors = []
    for name, patch in index.get('patches', {}).items():
        vc.safe_relative(name, folder=True)
        prefix = patch.get('repo_dir')
        if prefix != 'patches/' + name:
            raise ValueError('Invalid patch repository path.')
        for filename, info in list(patch['files'].items()) + [('patch.json', patch['patch_json'])]:
            vc.safe_relative(filename, asset=filename != 'patch.json')
            actual = objects.get(prefix + '/' + filename)
            if not actual or actual['mode'] not in ('100644', '100755') or actual['sha'] != info['sha'] or type(info['size']) is not int or measured.get(actual['sha']) != info['size']:
                errors.append(prefix + '/' + filename)
        raw = git_bytes(repo, 'show', 'HEAD:' + prefix + '/patch.json')
        metadata = strict_json(raw)
        vc.validate_entries(metadata.get('files'))
        if not isinstance(metadata.get('name'), str) or not metadata['name'].strip():
            errors.append(prefix + '/patch.json missing name')
        listed = {e['file']: e['id'] for e in metadata['files']}
        if listed != {k: v['id'] for k, v in patch['files'].items()}:
            errors.append(prefix + '/patch.json disagrees with index')
        if len(set(listed.values())) != len(listed):
            errors.append(prefix + '/patch.json duplicate IDs')
    if errors:
        raise ValueError('Publication snapshot is inconsistent; main was not changed. Repair: ' + '; '.join(errors[:8]))
    measured.save()
    return index
