using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;

namespace PrecisionStockpileControl
{
    // Scoped paste. Vanilla's Paste gizmo copies EVERYTHING: StorageSettings.CopyFrom carries the
    // filter + Priority band, and PSC's CopyFrom / PasteInto postfixes carry the full policy + feeder
    // routes. That clobbers a target's carefully-set routes and priorities. We keep the blunt full
    // paste on LEFT-click and add a RIGHT-click float menu of narrower modes.
    //
    // Seam: postfix StorageSettingsClipboard.CopyPasteGizmosFor — the single source feeding every
    // storage command bar (the Storage ITab shows no gizmo grid). The paste gizmo is the one with
    // hotKey Misc5; we wrap it so left-click stays vanilla and right-click offers the scoped modes.
    [HarmonyPatch(typeof(StorageSettingsClipboard), nameof(StorageSettingsClipboard.CopyPasteGizmosFor))]
    public static class StorageSettingsClipboard_CopyPasteGizmosFor_Patch
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, StorageSettings s)
        {
            foreach (var g in __result)
            {
                if (g is Command_Action cmd && cmd.hotKey == KeyBindingDefOf.Misc5)
                    yield return new Command_PscScopedPaste(cmd, s);
                else
                    yield return g;
            }
        }
    }

    // Wraps the vanilla Paste gizmo: left-click runs the original (full) paste unchanged; right-click
    // pops the scoped-paste float menu. When the clipboard is empty the wrapper inherits vanilla's
    // disabled state (a disabled Command never reaches the right-click branch).
    public class Command_PscScopedPaste : Command_Action
    {
        private readonly StorageSettings target;

        public Command_PscScopedPaste(Command_Action vanilla, StorageSettings target)
        {
            this.target = target;
            icon = vanilla.icon;
            defaultLabel = vanilla.defaultLabel;
            defaultDesc = "PSC_PasteScopedDesc".Translate();
            hotKey = vanilla.hotKey;
            action = vanilla.action;                 // left-click = vanilla full paste (sound + PasteInto)
            // Mirror vanilla: greyed out (and right-click suppressed) until something has been copied.
            if (!StorageSettingsClipboard.HasCopiedSettings) Disable();
        }

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                if (!StorageSettingsClipboard.HasCopiedSettings) yield break;
                yield return Option("PSC_PasteItemsLimits", () => PscScopedPaste.Apply(target, PscPasteScope.ItemsLimits));
                yield return Option("PSC_PasteItemsLimitsRoutes", () => PscScopedPaste.Apply(target, PscPasteScope.ItemsLimitsRoutes));
                yield return Option("PSC_PasteAllButPriorities", () => PscScopedPaste.Apply(target, PscPasteScope.EverythingButPriorities));
                // "Everything" duplicates left-click; kept in the menu for discoverability.
                yield return Option("PSC_PasteEverything", () => StorageSettingsClipboard.PasteInto(target));
            }
        }

        private static FloatMenuOption Option(string key, Action act)
        {
            return new FloatMenuOption(key.Translate(), () =>
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                act();
            });
        }
    }

    // The scoped paste itself. Reads the vanilla clipboard StorageSettings (the source) directly,
    // applies only the in-scope slice, and never touches the target's Priority band or fine-order.
    public static class PscScopedPaste
    {
        // The vanilla clipboard is a single reused private static StorageSettings; its PSC policy rode
        // the store entry keyed by that object at copy time (the CopyFrom postfix).
        private static readonly AccessTools.FieldRef<StorageSettings> ClipboardRef =
            AccessTools.StaticFieldRefAccess<StorageSettings>(
                AccessTools.Field(typeof(StorageSettingsClipboard), "clipboard"));

        public static void Apply(StorageSettings target, PscPasteScope scope)
        {
            if (target == null || !StorageSettingsClipboard.HasCopiedSettings) return;
            var clip = ClipboardRef();
            if (clip == null) return;

            // Items: vanilla allow/disallow list (every scope). CopyAllowancesFrom never touches Priority.
            target.filter.CopyAllowancesFrom(clip.filter);

            // PSC policy slice. A null source clears the in-scope PSC fields on the target; drop the
            // target's entry if that leaves it with no real policy.
            var srcData = PscStorageDataStore.TryGet(clip);
            var tgtData = PscStorageDataStore.TryGet(target);
            if (srcData != null || tgtData != null)
            {
                tgtData ??= PscStorageDataStore.GetOrCreate(target);
                tgtData.CopyScopedFrom(srcData, scope);
                if (!tgtData.HasPersistentPolicy) PscStorageDataStore.Remove(target);
            }

            // Feeder routes (routes scope and wider): replace the target's routes with the clipboard's.
            if (scope >= PscPasteScope.ItemsLimitsRoutes && PscLinkClipboard.HasData)
            {
                var unit = PscHaulUnit.ResolveSettings(target);
                if (unit.IsValid)
                    PscMapComponent.For(unit.Map)?.ApplyClipboardLinks(unit, PscLinkClipboard.Sources, PscLinkClipboard.Dests);
            }

            PscMapComponent.NotifyPolicyChanged(target);
            Messages.Message("StorageSettingsPastedFromClipboard".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }
}
