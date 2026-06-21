using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PrecisionStockpileControl
{
    // The single storage-mode gizmo shown on a selected stockpile / shelf (M5.2, Flickable-style).
    // One Command_Action whose icon reflects the current mode; clicking opens a float menu of all
    // four modes (one click to any mode — better than Flickable's click-to-cycle). The mode rides
    // PscStorageData.mode (shared across a linked StorageGroup), enforced by the IsForbidden freeze
    // postfix (StorageMode_Patches) and the AllowedToAccept haul-in gate (Admission_Patches).
    [StaticConstructorOnStartup]
    public static class PscModeGizmo
    {
        // Icon art reused from Mlie's Flickable Storage (MIT) — credited in About.xml / README.
        // Textures come from the shared catalog (PscStatusIcons) so each path lives in one place.
        private static readonly Texture2D OnTex = PscStatusIcons.ModeOnTex;
        private static readonly Texture2D OffTex = PscStatusIcons.ModeOffTex;
        private static readonly Texture2D AcceptTex = PscStatusIcons.ModeAcceptTex;
        private static readonly Texture2D RetrieveTex = PscStatusIcons.ModeRetrieveTex;

        private static readonly PscStorageMode[] AllModes =
        {
            PscStorageMode.Normal, PscStorageMode.Off, PscStorageMode.AcceptOnly, PscStorageMode.RetrieveOnly
        };

        public static IEnumerable<Gizmo> GizmosFor(StorageSettings settings, PscHaulUnit unit)
        {
            if (settings == null || !unit.IsValid) yield break;
            var current = PscStorageDataStore.TryGet(settings)?.mode ?? PscStorageMode.Normal;

            yield return new Command_Action
            {
                icon = IconFor(current),
                defaultLabel = LabelFor(current),
                defaultDesc = "PSC_StorageModeDesc".Translate(DescFor(current)),
                action = () => OpenMenu(settings, unit)
            };
        }

        private static void OpenMenu(StorageSettings settings, PscHaulUnit unit)
        {
            var options = new List<FloatMenuOption>(AllModes.Length);
            foreach (var m in AllModes)
            {
                var mode = m; // capture
                options.Add(new FloatMenuOption(LabelFor(mode), () => SetMode(settings, unit, mode),
                    IconFor(mode), Color.white));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void SetMode(StorageSettings settings, PscHaulUnit unit, PscStorageMode mode)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null || data.mode == mode) return;

            bool wasFreeze = IsFreeze(data.mode);
            bool nowFreeze = IsFreeze(mode);
            data.mode = mode;
            PscMapComponent.NotifyPolicyChanged(settings);

            // The freeze is virtual (no flag write), so a freeze transition doesn't dirty the haulables
            // list on its own. Poke the listers for the unit's contents exactly as CompForbiddable's
            // setter does, so the change takes effect immediately instead of on the next natural recalc.
            if (wasFreeze != nowFreeze) RefreshHaulables(unit, nowFreeze);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }

        private static void RefreshHaulables(PscHaulUnit unit, bool nowFrozen)
        {
            var map = unit.Map;
            var held = unit.HeldThings;
            if (map == null || held == null) return;
            // Snapshot defensively (ToList): HeldThings is grid-backed and not invalidated by the
            // lister Notify_* calls below, but a snapshot keeps this robust if HeldThings ever changes.
            foreach (var t in held.ToList())
            {
                if (t == null || !t.Spawned) continue;
                if (nowFrozen)
                {
                    map.listerHaulables.Notify_Forbidden(t);
                    map.listerMergeables.Notify_Forbidden(t);
                }
                else
                {
                    map.listerHaulables.Notify_Unforbidden(t);
                    map.listerMergeables.Notify_Unforbidden(t);
                }
            }
        }

        private static bool IsFreeze(PscStorageMode m) => m == PscStorageMode.Off || m == PscStorageMode.AcceptOnly;

        private static Texture2D IconFor(PscStorageMode m)
        {
            switch (m)
            {
                case PscStorageMode.Off: return OffTex;
                case PscStorageMode.AcceptOnly: return AcceptTex;
                case PscStorageMode.RetrieveOnly: return RetrieveTex;
                default: return OnTex;
            }
        }

        private static string LabelFor(PscStorageMode m) => ("PSC_Mode_" + m).Translate();
        private static string DescFor(PscStorageMode m) => ("PSC_Mode_" + m + "Desc").Translate();
    }
}
