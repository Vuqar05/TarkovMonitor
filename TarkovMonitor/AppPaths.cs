namespace TarkovMonitor
{
    /// <summary>
    /// Single source of truth for where Tarkov Monitor stores its own data.
    /// In portable mode (a portable.txt file or a Data folder next to the
    /// executable) everything is kept under &lt;exe dir&gt;\Data so nothing is
    /// written to the user profile. Otherwise the original per-user locations
    /// are returned unchanged so existing installs keep their data.
    /// </summary>
    internal static class AppPaths
    {
        private const string PortableMarkerFileName = "portable.txt";
        private const string PortableDataFolderName = "Data";

        private static readonly Lazy<bool> isPortable = new(() =>
            File.Exists(Path.Combine(ExecutableDirectory, PortableMarkerFileName))
            || Directory.Exists(Path.Combine(ExecutableDirectory, PortableDataFolderName)));

        public static string ExecutableDirectory => AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        public static bool IsPortable => isPortable.Value;

        public static string PortableDataRoot => Path.Combine(ExecutableDirectory, PortableDataFolderName);

        private static string LocalTarkovMonitorFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovMonitor");

        // Application.UserAppDataPath creates its folder when read, so it is
        // only touched outside portable mode.
        public static string StatsDatabasePath => IsPortable
            ? Path.Combine(PortableDataRoot, "TarkovMonitor.db")
            : Path.Join(Application.UserAppDataPath, "..", "TarkovMonitor.db");

        public static string CustomSoundsPath => IsPortable
            ? Path.Combine(PortableDataRoot, "sounds")
            : Path.Join(Application.UserAppDataPath, "..", "sounds");

        public static string DiagnosticsDirectory => IsPortable
            ? Path.Combine(PortableDataRoot, "Diagnostics")
            : Path.Combine(LocalTarkovMonitorFolder, "Diagnostics");

        public static string UpdateStagingRoot(string stagingFolderName) => IsPortable
            ? Path.Combine(PortableDataRoot, stagingFolderName)
            : Path.Combine(LocalTarkovMonitorFolder, stagingFolderName);

        /// <summary>
        /// WebView2 user data folder, or null to keep the WebView2 default.
        /// </summary>
        public static string? WebView2UserDataFolder => IsPortable
            ? Path.Combine(PortableDataRoot, "WebView2")
            : null;

        public static string PortableSettingsFile => Path.Combine(PortableDataRoot, "user.config");
    }
}
