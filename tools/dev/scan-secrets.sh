#!/usr/bin/env bash
# Scan the tracked tree for credential-shaped strings.
#
#   ./tools/dev/scan-secrets.sh            # working tree
#   ./tools/dev/scan-secrets.sh <rev>      # a commit, e.g. before pushing
#
# RUN THIS BEFORE EVERY MERGE, not just before a push.
#
# Why before a MERGE. A redaction is a deletion, and a deletion loses a merge
# against any branch that still holds the original. That is not hypothetical
# here: a live Mistral key was redacted on one slice, and merging a WIP commit
# that predated the redaction put the literal straight back into
# langflow/PLANNING-AGENT.md — and from there onto the remote.
#
# Why the patterns are shaped this way. The scan that missed it required the
# secret to be quoted ("[A-Za-z0-9]{32}"), which is true in JSON and false in
# Markdown prose. Everything below matches UNQUOTED forms too. The 32-char rule
# would otherwise fire on every long C# method name, so it is gated on Shannon
# entropy >= 3.9 bits/char, which random keys clear and identifiers do not.
#
# Exit codes: 0 clean, 1 findings. It prints file and line, never the value.
set -uo pipefail

REV="${1:-}"
cd "$(git rev-parse --show-toplevel)"

REV="$REV" python3 - <<'PY'
import subprocess, sys, re, math, collections, os

rev = os.environ.get("REV") or ""

if rev:
    files = subprocess.run(['git','ls-tree','-r','--name-only',rev],
                           capture_output=True, text=True).stdout.split('\n')
    read = lambda f: subprocess.run(['git','show',f'{rev}:{f}'], capture_output=True).stdout
else:
    files = subprocess.run(['git','ls-files'], capture_output=True, text=True).stdout.split('\n')
    def read(f):
        try:
            return open(f,'rb').read()
        except Exception:
            return b''

PATTERNS = {
    'signed JWT'         : re.compile(rb'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'),
    'mongodb+srv URI'    : re.compile(rb'mongodb\+srv://[^\s"\'<]+'),
    'azure AccountKey'   : re.compile(rb'AccountKey=[A-Za-z0-9+/=]{20,}'),
    'huggingface token'  : re.compile(rb'hf_[A-Za-z0-9]{20,}'),
    # \b, or every hyphenated identifier ending in "task" is a finding: the tail
    # of "updateTask-refuses-a-clashing-time" is a literal sk- followed by 20+
    # characters in the class. Three eval fixtures tripped it, the scanner
    # exited 1 on a clean tree, and a scanner that always fails is a scanner
    # nobody runs. A real key never begins mid-word.
    'sk- style key'      : re.compile(rb'\bsk-[A-Za-z0-9_-]{20,}'),
    'AWS access key id'  : re.compile(rb'AKIA[0-9A-Z]{16}'),
    'private key block'  : re.compile(rb'-----BEGIN [A-Z ]*PRIVATE KEY-----'),
    'high-entropy token' : re.compile(rb'(?<![A-Za-z0-9])[A-Za-z0-9]{32}(?![A-Za-z0-9])'),
}

# A redacted placeholder is the DESIRED state — never report it as a finding.
ALLOWED = re.compile(rb'REDACTED|PLACEHOLDER|EXAMPLE|not-a-real-key|dev-only-not-a-real', re.I)

def entropy(b: bytes) -> float:
    if not b:
        return 0.0
    counts = collections.Counter(b)
    n = len(b)
    return -sum(c/n * math.log2(c/n) for c in counts.values())

findings = 0
for f in files:
    if not f.strip():
        continue
    blob = read(f)
    if not blob or b'\0' in blob[:1024]:
        continue
    for lineno, line in enumerate(blob.split(b'\n'), 1):
        if ALLOWED.search(line):
            continue
        for label, pattern in PATTERNS.items():
            m = pattern.search(line)
            if not m:
                continue
            if label == 'high-entropy token':
                tok = m.group()
                # Identifiers clear the entropy bar too — CamelCase is not random
                # but it is varied. What separates them from a key is digits: a
                # 32-char run of pure letters is `SendAsyncFailsWithoutCalling`,
                # not a credential. Every hit this rule dropped on first run was a
                # method name, a test name, or an OpenAPI $ref.
                if entropy(tok) < 3.9 or not re.search(rb'[0-9]', tok):
                    continue
            # Value deliberately not printed.
            print(f'  !! {label:20s} {f}:{lineno}')
            findings += 1
            break

where = rev or 'working tree'
if findings:
    print(f'\n{findings} finding(s) in {where}. Re-redact, and remember the tip is not the '
          f'exposure — anything already pushed needs ROTATING, not just editing.')
    sys.exit(1)

print(f'  clean — no credential-shaped string in {where}')
PY
