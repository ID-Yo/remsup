"""Reject deployment/person/device details from the shareable support package."""
import pathlib
import re
import sys

PATTERNS = {
    'known-device-or-user': re.compile(r'\b(?:private-device-name|private-user-name)\b', re.I),
    'device-id': re.compile(r'(?<!\d)\d{9}(?!\d)'),
    'ip-address': re.compile(r'\b(?:\d{1,3}\.){3}\d{1,3}\b'),
    'absolute-personal-path': re.compile(r'\b[A-Za-z]:[\\/]'),
}

def findings(directory):
    failures=[]
    for path in sorted(pathlib.Path(directory).rglob('*')):
        if not path.is_file():
            continue
        try:
            lines=path.read_text(encoding='utf-8-sig').splitlines()
        except UnicodeError:
            failures.append((str(path.relative_to(directory)),0,'non-text-file'))
            continue
        for number,line in enumerate(lines,1):
            for category,pattern in PATTERNS.items():
                if pattern.search(line):
                    failures.append((str(path.relative_to(directory)),number,category))
    return failures

if __name__=='__main__':
    directory=pathlib.Path(__file__).resolve().parents[1]/'skills/remsup-support'
    failures=findings(directory)
    for file,line,category in failures:
        print(f'FAIL {file}:{line} {category}')
    print(f'{"FAIL" if failures else "PASS"} shareable skill data check')
    sys.exit(bool(failures))
