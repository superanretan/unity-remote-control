using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Maps RemoteListItem.presentationKey to a sprite that ships with the CONTROLLER build.
    // Images are never sent over the DataChannel — the host only names which one to use.
    [CreateAssetMenu(menuName = "Remote Control/Remote List Icon Set", fileName = "RemoteListIconSet")]
    public class RemoteListIconSet : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string presentationKey;
            public Sprite sprite;
        }

        [SerializeField] private Entry[] _entries = Array.Empty<Entry>();

        [Tooltip("Used when the key is empty or unknown. Optional.")]
        [SerializeField] private Sprite _fallback;

        public Sprite Resolve(string presentationKey)
        {
            if (string.IsNullOrEmpty(presentationKey)) return _fallback;

            for (int i = 0; i < _entries.Length; i++)
            {
                Entry entry = _entries[i];
                if (entry != null && entry.presentationKey == presentationKey)
                    return entry.sprite != null ? entry.sprite : _fallback;
            }

            return _fallback;
        }
    }
}
