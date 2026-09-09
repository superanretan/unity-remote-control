using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace SuperAnretan.RemoteControl
{
    // A list the host pushes to the controller so both render the same rows. Travels as JSON inside
    // HostMessage.payload — the same trick HostStateSnapshot uses, because JsonUtility cannot nest.
    //
    // Lives in the package rather than in either app: JsonUtility matches by FIELD NAME, so two
    // hand-copied DTOs that drift by one field lose that field silently, with no exception. One
    // shared type means host and controller are compiled against the same serializer.
    [Serializable]
    [Preserve]
    public class RemoteListItem
    {
        // Stable identity. The ONLY field that may be used to address the entity — labels and
        // ordinals are presentation and can change between generations.
        public string id;

        // Which sub-list this row belongs to, e.g. a tab name. Free-form, defined by the app.
        public string group;

        // Display name exactly as the host renders it, so both screens read identically.
        public string label;

        public string sublabel = string.Empty;

        // Key the controller resolves against its OWN local sprites. Images are never transferred.
        public string presentationKey = string.Empty;

        // Display number only, never identity.
        public string ordinal = string.Empty;

        public RemoteListItem() { }

        public RemoteListItem(string id, string group, string label)
        {
            this.id = id;
            this.group = group;
            this.label = label;
        }

        public override string ToString() => $"{id} [{group}] {label}";
    }

    [Serializable]
    [Preserve]
    public class RemoteListPayload
    {
        public const int CurrentProtocolVersion = 1;

        public int protocolVersion = CurrentProtocolVersion;

        // Bumped by the host whenever the addressable set changes. A command echoes the generation
        // it was rendered from so the host can reject a click made against a list it has replaced.
        public string generation = string.Empty;

        // What the list was built for (a room, a filter, a view). Diagnostic and validation only.
        public string contextId = string.Empty;

        public List<RemoteListItem> items = new();

        public RemoteListPayload() { }

        public RemoteListPayload(string generation, string contextId = "")
        {
            this.generation = generation;
            this.contextId = contextId ?? string.Empty;
        }

        // A malformed payload does NOT throw under JsonUtility — every field just deserializes to
        // its default. So callers must ask this before trusting anything they read.
        public bool IsValid =>
            protocolVersion == CurrentProtocolVersion && !string.IsNullOrEmpty(generation);

        public string ToJson() => JsonUtility.ToJson(this);

        public static RemoteListPayload FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var payload = JsonUtility.FromJson<RemoteListPayload>(json);
                if (payload == null) return null;
                payload.items ??= new List<RemoteListItem>();
                return payload;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteListPayload] Failed to deserialize: {e.Message}");
                return null;
            }
        }

        public override string ToString() =>
            $"gen={generation} context={contextId} items={items?.Count ?? 0}";
    }

    // Payload of a command that acts on one list item. Carrying the generation the row was rendered
    // from is what lets the host refuse a click made against a list it has already replaced,
    // instead of acting on whatever now sits in that position.
    [Serializable]
    [Preserve]
    public class RemoteListSelectArgs
    {
        public int protocolVersion = RemoteListPayload.CurrentProtocolVersion;
        public string generation = string.Empty;

        public RemoteListSelectArgs() { }

        public RemoteListSelectArgs(string generation)
        {
            this.generation = generation ?? string.Empty;
        }

        public bool IsValid =>
            protocolVersion == RemoteListPayload.CurrentProtocolVersion && !string.IsNullOrEmpty(generation);

        public string ToJson() => JsonUtility.ToJson(this);

        public static RemoteListSelectArgs FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<RemoteListSelectArgs>(json); }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteListSelectArgs] Failed to deserialize: {e.Message}");
                return null;
            }
        }

        public override string ToString() => $"v{protocolVersion} gen={generation}";
    }
}
