import os
import re

# 1. Find all C# files
csharp_files = []
yaml_files = []
for root, dirs, files in os.walk('Assets'):
    for file in files:
        path = os.path.join(root, file)
        if file.endswith('.cs'):
            csharp_files.append(path)
        elif file.endswith('.prefab') or file.endswith('.unity') or file.endswith('.asset'):
            yaml_files.append(path)

# 2. Extract mappings
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

print("Found", len(renames), "fields to rename.")

# 3. Replace in C# files
for path in csharp_files:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    
    new_content = content
    changed = False
    for old_name, new_name in renames.items():
        if old_name in new_content:
            new_content = re.sub(r'\b' + old_name + r'\b', new_name, new_content)
            changed = True
            
    if changed:
        with open(path, 'w', encoding='utf-8') as f:
            f.write(new_content)
        print("Updated C# file:", path)

# 4. Replace in YAML files
for path in yaml_files:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    
    new_content = content
    changed = False
    for old_name, new_name in renames.items():
        # YAML properties are serialized as '  _oldName: ' or '  _oldName: \n'
        pattern = r'^(\s*)' + old_name + r':'
        repl = r'\1' + new_name + r':'
        if re.search(pattern, new_content, flags=re.MULTILINE):
            new_content = re.sub(pattern, repl, new_content, flags=re.MULTILINE)
            changed = True
            
    if changed:
        with open(path, 'w', encoding='utf-8') as f:
            f.write(new_content)
        print("Updated YAML file:", path)

print("Done.")
