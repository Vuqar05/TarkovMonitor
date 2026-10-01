using System.Collections.Specialized;
using System.Configuration;
using System.Xml.Linq;

namespace TarkovMonitor.Properties
{
    /// <summary>
    /// Stores user settings in Data\user.config next to the executable when
    /// Tarkov Monitor runs in portable mode. Outside portable mode every call is
    /// delegated to the standard LocalFileSettingsProvider, so installed copies
    /// keep using their existing user.config under the Windows user profile.
    /// </summary>
    internal sealed class PortableSettingsProvider : SettingsProvider, IApplicationSettingsProvider
    {
        private const string ProviderName = nameof(PortableSettingsProvider);
        private const string RootElementName = "settings";
        private const string SettingElementName = "setting";
        private const string NameAttributeName = "name";

        private readonly LocalFileSettingsProvider fallback = new();
        private readonly object fileLock = new();
        private string applicationName = "TarkovMonitor";

        public override string ApplicationName
        {
            get => applicationName;
            set
            {
                applicationName = value;
                fallback.ApplicationName = value;
            }
        }

        public override string Name => ProviderName;

        public override void Initialize(string? name, NameValueCollection? config)
        {
            base.Initialize(string.IsNullOrEmpty(name) ? ProviderName : name, config);
            fallback.Initialize(nameof(LocalFileSettingsProvider), config);
        }

        public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection collection)
        {
            if (!AppPaths.IsPortable)
            {
                return fallback.GetPropertyValues(context, collection);
            }

            var stored = ReadStoredValues();
            var values = new SettingsPropertyValueCollection();
            foreach (SettingsProperty property in collection)
            {
                var value = new SettingsPropertyValue(property);
                if (IsUserScoped(property) && stored.TryGetValue(property.Name, out var serialized))
                {
                    value.SerializedValue = serialized;
                }
                value.IsDirty = false;
                values.Add(value);
            }
            return values;
        }

        public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection collection)
        {
            if (!AppPaths.IsPortable)
            {
                fallback.SetPropertyValues(context, collection);
                return;
            }

            lock (fileLock)
            {
                var stored = ReadStoredValues();
                foreach (SettingsPropertyValue value in collection)
                {
                    if (!IsUserScoped(value.Property))
                    {
                        continue;
                    }
                    if (value.UsingDefaultValue && !value.IsDirty)
                    {
                        stored.Remove(value.Name);
                        continue;
                    }
                    var serialized = value.SerializedValue;
                    stored[value.Name] = serialized switch
                    {
                        null => "",
                        string text => text,
                        byte[] bytes => Convert.ToBase64String(bytes),
                        _ => serialized.ToString() ?? "",
                    };
                }
                WriteStoredValues(stored);
            }
        }

        public SettingsPropertyValue GetPreviousVersion(SettingsContext context, SettingsProperty property)
        {
            if (!AppPaths.IsPortable)
            {
                return fallback.GetPreviousVersion(context, property);
            }
            // Portable settings are not versioned, so there is no previous copy.
            return new SettingsPropertyValue(property);
        }

        public void Reset(SettingsContext context)
        {
            if (!AppPaths.IsPortable)
            {
                fallback.Reset(context);
                return;
            }
            lock (fileLock)
            {
                WriteStoredValues(new Dictionary<string, string>());
            }
        }

        public void Upgrade(SettingsContext context, SettingsPropertyCollection properties)
        {
            if (!AppPaths.IsPortable)
            {
                fallback.Upgrade(context, properties);
            }
            // Portable settings live in one unversioned file, so there is nothing to upgrade.
        }

        private static bool IsUserScoped(SettingsProperty property) =>
            property.Attributes[typeof(UserScopedSettingAttribute)] is UserScopedSettingAttribute;

        private Dictionary<string, string> ReadStoredValues()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = AppPaths.PortableSettingsFile;
            lock (fileLock)
            {
                if (!File.Exists(path))
                {
                    return values;
                }
                XDocument document;
                try
                {
                    document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                }
                catch (System.Xml.XmlException)
                {
                    // Keep the unreadable file for inspection and start from
                    // defaults rather than failing every settings access.
                    File.Copy(path, path + ".corrupt", overwrite: true);
                    return values;
                }
                foreach (var element in document.Root?.Elements(SettingElementName) ?? Enumerable.Empty<XElement>())
                {
                    var name = element.Attribute(NameAttributeName)?.Value;
                    if (!string.IsNullOrEmpty(name))
                    {
                        values[name] = element.Value;
                    }
                }
            }
            return values;
        }

        private static void WriteStoredValues(Dictionary<string, string> values)
        {
            var path = AppPaths.PortableSettingsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var document = new XDocument(new XElement(RootElementName,
                values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new XElement(SettingElementName,
                        new XAttribute(NameAttributeName, pair.Key),
                        pair.Value))));

            // Write to a temporary file first so an interrupted save (for
            // example a removed flash drive) never truncates the settings file.
            var temporaryPath = path + ".tmp";
            document.Save(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
        }
    }
}
