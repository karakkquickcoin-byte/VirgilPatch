"""Encrypted GitHub publication. Secrets stay in WORK/weekly-passwords.json, never in Git.
VGL1 = AES-256-CBC + HMAC-SHA256 (encrypt then MAC), independent random 64-byte keys.
Password slots use PBKDF2-HMAC-SHA256/600000 with random 16-byte salts.
"""
import base64, datetime as dt, hashlib, hmac, io, json, os, re, sys, tempfile, uuid
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'lib'))
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives import padding
import virgil_core as vc
import virgil_versions as vv

ITERATIONS = 600000

def b64(b): return base64.b64encode(b).decode('ascii')
def un64(s): return base64.b64decode(s, validate=True)
def dumps(v): return json.dumps(v, separators=(',', ':'), ensure_ascii=True).encode('utf-8')
def now(): return dt.datetime.now(dt.timezone.utc)
def secret_path(): return os.path.join(vc.WORK, 'weekly-passwords.json')
def settings(): return vc.read_json(secret_path()) if os.path.isfile(secret_path()) else None
def enabled(): return settings() is not None

def set_password(password, master=None, clock=None):
    clock = clock or now()
    if len(password) < 10: raise ValueError('Use at least 10 characters for the weekly password.')
    data = settings() or {'format': 1, 'history': []}
    if not data.get('recovery_password') and not master: raise ValueError('Set the recovery password first.')
    if master: data['recovery_password'] = master
    if password == data['recovery_password'] or any(r['password'] == password for r in data['history']):
        raise ValueError('Choose a new weekly password, different from recovery and all previous weeks.')
    row = {'number': '0.' + str(len(data['history']) + 1), 'password': password,
           'created': clock.isoformat(), 'expires': (clock + dt.timedelta(days=7)).isoformat(),
           'gate_salt': b64(os.urandom(16))}
    data['history'].append(row)
    # Keep outside the repository, even if configuration points at an unusual WORK.
    if os.path.commonpath([os.path.abspath(vc.REPO), os.path.abspath(secret_path())]) == os.path.abspath(vc.REPO):
        raise ValueError('The password record must be outside the Git repository.')
    os.makedirs(vc.WORK, exist_ok=True)
    vc.atomic_json(secret_path(), data, indent=2)
    return row

def current(require=False):
    data = settings()
    if not data or not data['history']:
        if require: raise ValueError('Set this week\'s password in Publisher before pushing.')
        return None
    row = data['history'][-1]
    if require and now() >= dt.datetime.fromisoformat(row['expires']):
        raise ValueError('Pass ' + row['number'] + ' has expired. Set a new weekly password before pushing.')
    return row

def derive(password, salt): return hashlib.pbkdf2_hmac('sha256', password.encode('utf-8'), salt, ITERATIONS, 64)

# ---------------------------------------------------------------- Update-button gate (default mode, 2026-11-19)
# Patch files stay plain on GitHub exactly as before (no re-upload, no full re-download for players). index.json
# only carries a salted PBKDF2 check of this week's code, and the updater asks for that code before it downloads.
# A spoiler blocker for people who click Update early: anyone fetching the raw GitHub mirror still gets the files.
# Salts stay fixed for the whole Pass so the updater can remember an unlocked Pass across launches and pushes.

def _check(password, salt_b64):
    return b64(hashlib.pbkdf2_hmac('sha256', password.encode('utf-8'), un64(salt_b64), ITERATIONS, 32))

def gate(clock=None):
    data = settings()
    if not data or not data.get('history') or not data.get('recovery_password'):
        raise ValueError('Set this week\'s password in Publisher before pushing.')
    row = data['history'][-1]
    if (clock or now()) >= dt.datetime.fromisoformat(row['expires']):
        raise ValueError('Pass ' + row['number'] + ' has expired. Set a new weekly password before pushing.')
    changed = False
    if not row.get('gate_salt'): row['gate_salt'] = b64(os.urandom(16)); changed = True
    if not data.get('recovery_gate_salt'): data['recovery_gate_salt'] = b64(os.urandom(16)); changed = True
    if changed: vc.atomic_json(secret_path(), data, indent=2)
    return {'format': 1, 'pass_number': row['number'], 'iterations': ITERATIONS,
            'weekly': {'salt': row['gate_salt'], 'check': _check(row['password'], row['gate_salt'])},
            'recovery': {'salt': data['recovery_gate_salt'], 'check': _check(data['recovery_password'], data['recovery_gate_salt'])}}

def apply_gate(index):
    """Add (or drop, when no weekly password is configured) the gate on an index about to be committed."""
    if enabled(): index['gate'] = gate()
    else: index.pop('gate', None)
    return index

def encrypt(source, target, key):
    iv = os.urandom(16); header = b'VGL1' + iv
    mac = hmac.new(key[32:], header, hashlib.sha256)
    target.write(header)
    enc = Cipher(algorithms.AES(key[:32]), modes.CBC(iv)).encryptor()
    pad = padding.PKCS7(128).padder()
    while True:
        block = source.read(1024 * 1024)
        if not block: break
        out = enc.update(pad.update(block)); target.write(out); mac.update(out)
    out = enc.update(pad.finalize()) + enc.finalize(); target.write(out); mac.update(out)
    target.write(mac.digest())

def seal(data, key):
    out = io.BytesIO(); encrypt(io.BytesIO(data), out, key); return out.getvalue()

def open_sealed(data, key):
    if len(data) < 68 or data[:4] != b'VGL1' or (len(data)-52) % 16: raise ValueError('Invalid encrypted payload.')
    if not hmac.compare_digest(hmac.new(key[32:], data[:-32], hashlib.sha256).digest(), data[-32:]):
        raise ValueError('Incorrect password or damaged encrypted payload.')
    dec = Cipher(algorithms.AES(key[:32]), modes.CBC(data[4:20])).decryptor()
    pad = padding.PKCS7(128).unpadder(); clear = dec.update(data[20:-32]) + dec.finalize()
    return pad.update(clear) + pad.finalize()

def envelope(catalog, row, recovery):
    key = os.urandom(64)
    result = {'format': 3, 'locked': True, 'pass_number': row['number'], 'iterations': ITERATIONS,
              'catalog': b64(seal(dumps(catalog), key))}
    for label, password in [('weekly', row['password']), ('recovery', recovery)]:
        salt = os.urandom(16)
        result[label] = {'salt': b64(salt), 'key': b64(seal(key, derive(password, salt)))}
    return result

def unlock(outer, password, slots=('weekly', 'recovery')):
    if outer.get('format') != 3 or outer.get('locked') is not True or outer.get('iterations') != ITERATIONS:
        raise ValueError('Unsupported encrypted publication.')
    for label in slots:
        slot = outer[label]; salt = un64(slot['salt'])
        if len(salt) != 16: raise ValueError('Invalid password salt.')
        try: key = open_sealed(un64(slot['key']), derive(password, salt))
        except ValueError: continue
        if len(key) != 64: raise ValueError('Invalid catalog key.')
        return vc.strict_json(open_sealed(un64(outer['catalog']), key))
    raise ValueError('Incorrect password for Pass ' + outer['pass_number'])

def local_catalog(outer):
    data = settings()
    if not data: raise ValueError('Local password record is missing; restore it before publishing.')
    # The publisher holds the recovery password, so only that slot can open; trying the weekly slot first
    # cost a wasted 600k-round PBKDF2 (~1 s) on every catalog read.
    return unlock(outer, data['recovery_password'], slots=('recovery',))

def logical(outer): return local_catalog(outer)['index'] if outer.get('locked') else outer

def git_catalog(pub):
    raw = pub.git('show', 'HEAD:index.json', check_rc=False)
    if not raw.strip(): return {'index': {'format': 2, 'patches': {}}, 'objects': {}}
    value = vc.strict_json(raw)
    if value.get('locked'): return local_catalog(value)
    return {'index': value, 'objects': {}}

def verify_head(repo):
    import virgil_integrity as vi
    outer = vc.strict_json(vi.git_bytes(repo, 'show', 'HEAD:index.json'))
    catalog = local_catalog(outer)
    import virgil_publish as pub
    pub.validate_index(catalog['index'])
    tracked = vi.records(repo, 'HEAD')
    expected = set()
    for name, patch in catalog['index']['patches'].items():
        for filename, info in list(patch['files'].items()) + [('patch.json', patch['patch_json'])]:
            logical_path = patch['repo_dir'] + '/' + filename
            obj = catalog['objects'].get(logical_path)
            if not obj or not re.fullmatch(r'locked/[a-f0-9]{32}\.vlock', obj['path']):
                raise ValueError('Missing encrypted object.')
            expected.add(obj['path'])
            actual = tracked.get(obj['path'])
            if not actual or actual['sha'] != obj['sha']: raise ValueError('Encrypted snapshot checksum mismatch.')
    if any(p.startswith('patches/') for p in tracked): raise ValueError('Plaintext patch files remain in the encrypted snapshot.')
    actual_locked = {p for p in tracked if p.startswith('locked/')}
    if expected != actual_locked: raise ValueError('Encrypted snapshot contains unexpected objects.')
    return catalog['index']

def publish(pub, selected=None, remove=None, push=True, deep=False, say=print):
    # Full encryption mode. No longer called by the publisher (which uses apply_gate instead); kept for reference
    # and for its tests. Its first run re-encrypts and re-uploads every published folder.
    row = current(require=True)
    pub.require_clean_stage()
    if push: pub.refresh_remote(say)
    # Do not upload pre-lock plaintext commits that were never on the public branch.
    remote = pub.git('rev-parse', '--verify', '-q', 'origin/main', check_rc=False).strip()
    if push and remote:
        for commit in pub.git('rev-list', 'origin/main..HEAD').split():
            raw = pub.git('show', commit + ':index.json', check_rc=False)
            if raw.strip() and not vc.strict_json(raw).get('locked'):
                raise ValueError('Unpublished plaintext commits are pending. Reconcile those before encrypted publication; none were uploaded.')
    old_catalog = git_catalog(pub)
    before = old_catalog['index']; idx = json.loads(json.dumps(before)); idx['format'] = 2
    idx.setdefault('patches', {})
    objects = dict(old_catalog['objects'])
    published = vc.load_published()
    sources = {pub.pname(p): p for p in published}
    names = list(sources) if selected is None else ([] if remove is not None else [pub.pname(selected)])
    # The first encrypted snapshot migrates all current folders; every byte comes from its source.
    if not old_catalog['objects']: names = list(sources)
    if selected is not None and remove is None and pub.pname(selected) not in sources: raise ValueError('Folder is not in the published list.')
    scans = {}
    for name in names:
        path = sources[name]
        vi = pub.vi
        vi.repair_source_filenames(path, idx['patches'].get(name, {}).get('files', {}), say=say)
        sc = pub.scan_folder(path)
        if sc.get('unregistered_wmos'): raise ValueError(pub.scan_summary(sc))
        report = pub.quick_check(sc, published, deep)
        if not report['ok']: return {'ok': False, 'errors': report['errors']}
        scans[name] = sc
    os.makedirs(os.path.join(vc.REPO, 'locked'), exist_ok=True)
    def put(logical_path, info, source=None, raw=None, previous_path=None, previous_info=None):
        if info['size'] > (16 * 1024 * 1024 if logical_path.endswith('/patch.json') else 2 * 1024 * 1024 * 1024):
            raise ValueError('File exceeds the updater size limit: ' + logical_path)
        prior_obj = objects.get(previous_path)
        if prior_obj and previous_info == info:
            objects[logical_path] = prior_obj
            return
        key = os.urandom(64); rel = 'locked/' + uuid.uuid4().hex + '.vlock'
        dest = vc.path_in(vc.REPO, rel)
        with (open(source, 'rb') if source else io.BytesIO(raw)) as inp, open(dest + '.tmp', 'xb') as out:
            encrypt(inp, out, key)
        if source and (os.path.getsize(source) != info['size'] or vc.git_blob_sha(source) != info['sha']):
            os.unlink(dest + '.tmp')
            raise ValueError('Source changed during encryption; retry publication.')
        os.replace(dest + '.tmp', dest)
        objects[logical_path] = {'path': rel, 'key': b64(key), 'size': os.path.getsize(dest), 'sha': vc.git_blob_sha(dest)}
    for name, sc in scans.items():
        prior = idx['patches'].get(name, {})
        changed = sc['first'] or sc['new'] or sc['changed'] or sc['removed'] or sc['patch_json_changed']
        version = vv.next_version(prior) if changed or not prior.get('version') else prior['version']
        prefix = 'patches/' + vv.display_name(name, version)
        patch = vc.Patch(sources[name])
        meta = dict(vv.metadata(patch.meta, name, version), files=patch.entries)
        raw = (json.dumps(meta, indent=2, ensure_ascii=False) + '\n').encode('utf-8')
        pj = {'size': len(raw), 'sha': hashlib.sha1(('blob %d\0' % len(raw)).encode() + raw).hexdigest()}
        for filename, info in sc['files'].items():
            put(prefix + '/' + filename, info, source=vc.path_in(sources[name], filename),
                previous_path=prior.get('repo_dir', '') + '/' + filename, previous_info=prior.get('files', {}).get(filename))
        put(prefix + '/patch.json', pj, raw=raw, previous_path=prior.get('repo_dir', '') + '/patch.json', previous_info=prior.get('patch_json'))
        idx['patches'][name] = {'repo_dir': prefix, 'version': version, 'display_name': vv.display_name(name, version),
            'source_manifest': vv.manifest_hash(patch.meta), 'files': sc['files'], 'patch_json': pj,
            'total_bytes': sum(v['size'] for v in sc['files'].values()),
            'changes': {'published': now().isoformat(), **{k + '_count': len(sc[k]) for k in ('new', 'changed', 'removed')}}}
        # Match the normal release change schema.
        idx['patches'][name]['changes'] = {'published': now().isoformat(),
            **{k + '_count': len(sc[s]) for k,s in [('added','new'),('changed','changed'),('removed','removed')]},
            **{k: sorted(sc[s])[:100] for k,s in [('added','new'),('changed','changed'),('removed','removed')]}}
    if remove is not None: idx['patches'].pop(remove, None)
    keep = {p['repo_dir'] + '/' + f for p in idx['patches'].values() for f in list(p['files']) + ['patch.json']}
    objects = {k:v for k,v in objects.items() if k in keep}
    if set(objects) != keep: raise ValueError('A patch source is missing; no partial encrypted publication was committed.')
    idx['published'] = now().isoformat()
    pub.validate_index(idx)
    outer = envelope({'index':idx, 'objects':objects}, row, settings()['recovery_password'])
    if len(dumps(outer)) > 64 * 1024 * 1024:
        raise ValueError('Encrypted catalog exceeds the updater size limit; no publication was staged.')
    vc.atomic_json(os.path.join(vc.REPO, 'index.json'), outer, separators=(',', ':'))
    # Remove plaintext from Git's next snapshot without deleting the source installation or sparse worktree.
    pub.git('rm', '-r', '--sparse', '--cached', '--ignore-unmatch', '--', 'patches')
    active = {v['path'] for v in objects.values()}
    tracked = pub.vi.records(vc.REPO)
    stale = [p for p in tracked if p.startswith('locked/') and p not in active]
    if stale: pub.git('update-index', '--force-remove', '-z', '--stdin', input='\0'.join(stale) + '\0')
    # Stage only ciphertext. Private records cannot enter through a broad git add.
    batch, batch_size = [], 0
    for rel in sorted(active):
        if rel in tracked: continue
        size = os.path.getsize(vc.path_in(vc.REPO, rel))
        if batch and batch_size + size > 400_000_000:
            pub.git('add', '--pathspec-from-file=-', '--pathspec-file-nul', input='\0'.join(batch))
            pub.git('commit', '-q', '-m', 'Encrypted upload batch - Pass ' + row['number'])
            batch, batch_size = [], 0
        batch.append(rel); batch_size += size
    if batch: pub.git('add', '--pathspec-from-file=-', '--pathspec-file-nul', input='\0'.join(batch))
    pub.git('add', '--', 'index.json')
    pub.git('commit', '-q', '-m', 'Publish encrypted patches - Pass ' + row['number'])
    verify_head(vc.REPO)
    pub.save_hash_cache()
    result = {'ok':True, 'commits':1, 'changes':'Encrypted publication - Pass ' + row['number']}
    if push: result['pushed'] = pub.push_pending(say)
    return result

