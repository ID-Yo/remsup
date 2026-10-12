"""Export the reviewed HEAD allowlist to a new directory without Git history."""
import argparse, json, pathlib, subprocess
p=argparse.ArgumentParser();p.add_argument('destination');args=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];destination=pathlib.Path(args.destination).resolve()
if destination.exists(): raise SystemExit('Destination must not exist')
if root==destination or root in destination.parents: raise SystemExit('Export outside the checkout')
if subprocess.check_output(['git','-C',str(root),'status','--porcelain']): raise SystemExit('Commit all inputs first')
subprocess.run(['python','-B',str(root/'scripts/scan-public.py')],check=True)
names=json.loads((root/'export-allowlist.json').read_text(encoding='utf-8'))
tracked=set(subprocess.check_output(['git','-C',str(root),'ls-files'],text=True).splitlines())
if set(names)!=tracked: raise SystemExit('Allowlist must equal the reviewed tracked snapshot')
destination.mkdir()
for name in names:
 path=pathlib.PurePosixPath(name)
 if path.is_absolute() or '..' in path.parts: raise SystemExit('Invalid allowlist path')
 data=subprocess.check_output(['git','-C',str(root),'show','HEAD:'+name])
 target=destination/name;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
print(f'Exported {len(names)} files without repository metadata')
