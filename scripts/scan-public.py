"""Scan tracked source only. Findings contain locations/categories, never values."""
import pathlib, re, subprocess, sys
ROOT=pathlib.Path(__file__).resolve().parents[1]
PATTERNS={
 'private-key':re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----'),
 'credential-token':re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9_]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[A-Z0-9]{16}|sk-[A-Za-z0-9]{30,})\b'),
 'personal-path':re.compile(r'[A-Za-z]:[\\/](?:Users|Coding)[\\/]',re.I),
 'jwt':re.compile(r'\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\b'),
}
BLOCKED={'memory','artifacts','releases','output','tmp','handoffs','node_modules','bin','obj'}
BINARY={'.exe','.dll','.pfx','.pem','.key','.png','.ico','.jpg','.zip','.pyc'}
REVIEW={
 'address-or-version':re.compile(r'\b(?:\d{1,3}\.){3}\d{1,3}\b'),
 'device-like-number':re.compile(r'(?<![\w])\d{9}(?![\w])'),
 'email':re.compile(r'\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b'),
 'url-or-repository':re.compile(r'https?://[^\s"<>]+'),
 'ssh-route':re.compile(r'\b(?:ssh|scp)\b',re.I),
}
def inspect(name,data):
 findings=[]
 path=pathlib.PurePosixPath(name)
 if set(path.parts)&BLOCKED or path.suffix.lower() in BINARY or (path.suffix.lower()=='.md' and 'HANDOFF' in path.name.upper()): findings.append((name,0,'excluded-file'))
 try: lines=data.decode('utf-8-sig').splitlines()
 except UnicodeError: return findings+[(name,0,'non-text')]
 for number,line in enumerate(lines,1):
  for category,pattern in PATTERNS.items():
   if pattern.search(line): findings.append((name,number,category))
 return findings
def main():
 paths=subprocess.check_output(['git','-C',str(ROOT),'ls-files','-z']).decode().split('\0')
 findings=[]
 for name in filter(None,paths):
  data=(ROOT/name).read_bytes();findings.extend(inspect(name,data))
  if '--review' in sys.argv:
   for number,line in enumerate(data.decode('utf-8-sig').splitlines(),1):
    for category,pattern in REVIEW.items():
     if pattern.search(line): print(f'REVIEW {name}:{number} {category}')
 for name,line,category in findings: print(f'FAIL {name}:{line} {category}')
 print(f'{"FAIL" if findings else "PASS"} public content scan ({len(list(filter(None,paths)))} tracked text files)')
 return bool(findings)
if __name__=='__main__': sys.exit(main())
