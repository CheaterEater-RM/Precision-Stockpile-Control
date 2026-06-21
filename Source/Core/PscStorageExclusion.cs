using Verse;

namespace PrecisionStockpileControl
{
    // XML-only marker: any storage ThingDef carrying this mod extension hides PSC's storage-tab button
    // and feeder controls. This is the definitive way to exclude additional (e.g. modded) storage now
    // that the in-menu blacklist is gone — patch a ThingDef:
    //
    //   <Operation Class="PatchOperationAddModExtension">
    //     <xpath>/Defs/ThingDef[defName="SomeStorage"]</xpath>
    //     <value><li Class="PrecisionStockpileControl.PscStorageExclusion" /></value>
    //   </Operation>
    //
    // The built-in single-purpose blacklist (PscStorageButtonFilter) is separate and always applies.
    public class PscStorageExclusion : DefModExtension
    {
    }
}
