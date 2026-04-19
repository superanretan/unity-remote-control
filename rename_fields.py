import os
import re

csharp_files = []
for root, dirs, files in os.walk('Assets'):
    for file in files:
        if file.endswith('.cs'):
            csharp_files.append(os.path.join(root, file))

renames = set()
for path in csharp_files:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # Match [SerializeField] private Type _name[ = ...];
    # We'll use a simpler regex to catch the name
    matches = re.finditer(r'\[SerializeField\](?:\s+\[[^\]]+\])*\s+(?:private\s+)?(?:readonly\s+)?([A-Za-z0-9_<>\[\]]+)\s+(_[A-Za-z0-9_]+)', content)
    for m in matches:
        type_name = m.group(1)
        old_name = m.group(2)
        if old_name.startswith('_'):
            new_name = old_name[1].lower() + old_name[2:] if len(old_name) > 1 else old_name
            renames.add((old_name, new_name))

for old, new in sorted(renames):
    print(f"{old} -> {new}")
