using System.Configuration;

namespace TarkovMonitor.Properties
{
    // Routes settings storage through PortableSettingsProvider, which keeps the
    // standard per-user storage unless Tarkov Monitor runs in portable mode.
    [SettingsProvider(typeof(PortableSettingsProvider))]
    internal sealed partial class Settings
    {
    }
}
