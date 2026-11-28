"""Install the Release build of the editor over the self-contained desktop copy.

usage: python tools/install-desktop.py <backup-tag> [--dest <folder>]

The desktop copy is a SELF-CONTAINED publish: its RefractorForge.deps.json lists the runtime pack, and replacing that
file with the build output's (framework-dependent) one stops the editor from starting at all. So this copies only the
project's own assemblies and the managed libraries that are new since the copy was published, and adds those new
libraries to the EXISTING manifest - a self-contained host loads nothing the manifest does not list.
Everything it replaces is kept in <dest>/_prev_<backup-tag>/ first.
"""
import json, os, shutil, sys

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
build = os.path.join(repo, 'src', 'RefractorForge.Viewer', 'bin', 'Release', 'net10.0-windows')
dest = r'C:\Users\lucas\Desktop\RefractorForge\RefractorForge'
if '--dest' in sys.argv: dest = sys.argv[sys.argv.index('--dest') + 1]
tag = sys.argv[1]
prev = os.path.join(dest, '_prev_' + tag)

OWN = ['RefractorForge.dll', 'RefractorForge.exe', 'RefractorForge.Formats.dll', 'RefractorForge.Render.dll',
       'RefractorForge.Collab.dll', 'RefractorForge.VkTrace.dll', os.path.join('lang', 'ja.json')]

dev = json.load(open(os.path.join(build, 'RefractorForge.deps.json'), encoding='utf-8-sig'))
inst_path = os.path.join(dest, 'RefractorForge.deps.json')
inst = json.load(open(inst_path, encoding='utf-8-sig'))
dev_target = next(v for k, v in dev['targets'].items() if v)
inst_key = next(k for k in inst['targets'] if 'win-x64' in k)
inst_target = inst['targets'][inst_key]

# Libraries the build has and the desktop manifest does not: managed ones only (a "runtime" section with a .dll).
new_libs = []
for name, entry in dev_target.items():
    if name in inst_target or name.startswith('RefractorForge/'): continue
    dlls = [p for p in entry.get('runtime', {}) if p.lower().endswith('.dll')]
    if not dlls: continue
    if entry.get('native') or entry.get('runtimeTargets'):
        sys.exit(f'{name} brings native files - re-publish self-contained instead of hand-installing.')
    new_libs.append((name, entry, dlls))

files = list(OWN)
for name, entry, dlls in new_libs:
    for p in dlls: files.append(os.path.basename(p))
guide = os.path.join(repo, 'USER_GUIDE.md')

os.makedirs(os.path.join(prev, 'lang'), exist_ok=True)
shutil.copy2(inst_path, os.path.join(prev, 'RefractorForge.deps.json'))
for f in files:
    src = os.path.join(build, f)
    if not os.path.exists(src): sys.exit('missing in the build output: ' + f)
    dst = os.path.join(dest, f)
    if os.path.exists(dst): shutil.copy2(dst, os.path.join(prev, f))
    shutil.copyfile(src, dst)
if os.path.exists(os.path.join(dest, 'USER_GUIDE.md')): shutil.copy2(os.path.join(dest, 'USER_GUIDE.md'), os.path.join(prev, 'USER_GUIDE.md'))
shutil.copyfile(guide, os.path.join(dest, 'USER_GUIDE.md'))

if new_libs:
    for name, entry, dlls in new_libs:
        inst_target[name] = {k: v for k, v in entry.items() if k in ('dependencies', 'runtime')}
        inst['libraries'][name] = dev['libraries'][name]
    # The editor's own dependency list, so the new libraries hang off it as they do in the build.
    app_key = next(k for k in inst_target if k.startswith('RefractorForge/'))
    dev_app = next(v for k, v in dev_target.items() if k.startswith('RefractorForge/'))
    for dep, ver in dev_app.get('dependencies', {}).items():
        if any(dep + '/' + ver == n for n, _, _ in new_libs): inst_target[app_key].setdefault('dependencies', {})[dep] = ver
    with open(inst_path, 'w', encoding='utf-8', newline='\n') as f: json.dump(inst, f, indent=2)

print('installed', len(files), 'file(s);', 'new libraries added to the manifest:', ', '.join(n for n, _, _ in new_libs) or 'none')
print('backup:', prev)
