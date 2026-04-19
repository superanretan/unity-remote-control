import os
import re

csharp_files = []
for root, dirs, files in os.walk('Assets'):
    for file in files:
        if file.endswith('.cs'):
            csharp_files.append(os.path.join(root, file))

renames = {}
for path in csharp_files:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    
    matches = re.finditer(r'\[SerializeField\](?:\s+\[[^\]]+\])*\s+(?:private\s+)?(?:readonly\s+)?(?:[A-Za-z0-9_<>\[\]]+)\s+(_[A-Za-z0-9_]+)', content)
    for m in matches:
        old_name = m.group(1)
        if old_name.startswith('_'):
            new_name = old_name[1].lower() + old_name[2:] if len(old_name) > 1 else old_name
            renames[old_name] = new_name

conflicts = []
for path in csharp_files:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    for old_name, new_name in renames.items():
        if old_name in content:
            # Check if new_name is used as a parameter or local variable near an assignment
            # Simple heuristic: if the file has " new_name" or "(new_name" we might have a conflict
            if re.search(r'\b' + new_name + r'\b', content) and old_name in content:
                # Let's check if there's an assignment like _old_name = new_name
                if re.search(old_name + r'\s*=\s*' + new_name, content) or re.search(new_name + r'\s*=\s*' + old_name, content):
                    conflicts.append((path, old_name, new_name))

for c in conflicts:
    print("Conflict:", c)
