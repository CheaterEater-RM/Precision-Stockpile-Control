using System;
using System.Reflection;

namespace PrecisionStockpileControl.Tests
{
    internal sealed class PscSettingsScope : IDisposable
    {
        private static readonly FieldInfo SettingsField = typeof(PscMod).GetField(
            "<Settings>k__BackingField",
            BindingFlags.Static | BindingFlags.NonPublic);

        private readonly PscSettings previous;

        public PscSettings Settings { get; }

        public PscSettingsScope(Action<PscSettings> configure = null)
        {
            previous = PscMod.Settings;
            Settings = new PscSettings();
            configure?.Invoke(Settings);
            Set(Settings);
        }

        public void Dispose() => Set(previous);

        private static void Set(PscSettings settings)
        {
            if (SettingsField == null)
                throw new MissingFieldException(typeof(PscMod).FullName, "<Settings>k__BackingField");
            SettingsField.SetValue(null, settings);
        }
    }
}
