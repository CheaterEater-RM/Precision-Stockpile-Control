using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Single source of truth for the policy status-icon textures and the "which icons are active for
    // this storage" logic. The on-map overlay (PscStorageOverlay) reads icons from here rather than
    // reaching into PscToggleStrip / PscModeGizmo internals; those two surfaces source their textures
    // here too, so each texture path lives in exactly one place.
    [StaticConstructorOnStartup]
    public static class PscStatusIcons
    {
        [Flags]
        public enum Flag
        {
            None       = 0,
            Mode       = 1 << 0,   // storage mode != Normal (icon varies by mode value — use ModeTex)
            Limits     = 1 << 1,   // any per-def or default item limit set
            BatchFill  = 1 << 2,
            BatchEmpty = 1 << 3,
            OnlyFrom   = 1 << 4,
            OnlyTo     = 1 << 5,
            Alarm      = 1 << 6,
        }

        // Toggle / policy icons (shared with PscToggleStrip).
        public static readonly Texture2D BatchInTex  = Load("UI/Toggles/BatchIn");
        public static readonly Texture2D BatchOutTex = Load("UI/Toggles/BatchOut");
        public static readonly Texture2D OnlyFromTex = Load("UI/Toggles/OnlyFromSource");
        public static readonly Texture2D OnlyToTex   = Load("UI/Toggles/OnlyToDestinations");
        public static readonly Texture2D AlarmTex    = Load("UI/Toggles/Alarm");
        public static readonly Texture2D LimitsTex   = Load("UI/Widgets/PSC_LimitI");

        // Mode icons (shared with PscModeGizmo).
        public static readonly Texture2D ModeOnTex       = Load("UI/Mode/On");
        public static readonly Texture2D ModeOffTex      = Load("UI/Mode/Off");
        public static readonly Texture2D ModeAcceptTex   = Load("UI/Mode/AcceptOnly");
        public static readonly Texture2D ModeRetrieveTex = Load("UI/Mode/RetrieveOnly");

        private static Texture2D Load(string path) => ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;

        // The set of icons that should appear for a unit's policy. Null data (untouched storage)
        // resolves to None so the overlay draws priority only. Never mutates / creates data.
        public static Flag Resolve(PscStorageData data)
        {
            if (data == null) return Flag.None;
            Flag f = Flag.None;
            if (data.mode != PscStorageMode.Normal) f |= Flag.Mode;
            if (data.HasAnyLimit) f |= Flag.Limits;
            if (data.batch > 0) f |= Flag.BatchFill;
            if (data.batchEmpty > 0) f |= Flag.BatchEmpty;
            if (data.onlyFromSource) f |= Flag.OnlyFrom;
            if (data.onlyToDestinations) f |= Flag.OnlyTo;
            if (data.alarm != null && data.alarm.IsActive) f |= Flag.Alarm;
            return f;
        }

        // Texture for a single non-Mode flag. Mode is mode-value dependent — use ModeTex.
        public static Texture2D TextureFor(Flag single)
        {
            switch (single)
            {
                case Flag.Limits:     return LimitsTex;
                case Flag.BatchFill:  return BatchInTex;
                case Flag.BatchEmpty: return BatchOutTex;
                case Flag.OnlyFrom:   return OnlyFromTex;
                case Flag.OnlyTo:     return OnlyToTex;
                case Flag.Alarm:      return AlarmTex;
                default:              return BaseContent.BadTex;
            }
        }

        public static Texture2D ModeTex(PscStorageMode mode)
        {
            switch (mode)
            {
                case PscStorageMode.Off:          return ModeOffTex;
                case PscStorageMode.AcceptOnly:   return ModeAcceptTex;
                case PscStorageMode.RetrieveOnly: return ModeRetrieveTex;
                default:                          return ModeOnTex;
            }
        }
    }
}
