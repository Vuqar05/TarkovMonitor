using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TarkovMonitor
{
    /// <summary>
    /// Presses the user's chosen screenshot key at a fixed interval while a raid
    /// is in progress, so EFT saves a screenshot (and with it the player's
    /// position) without the player pressing the key manually.
    ///
    /// It only uses the raid start/end events already parsed from EFT's log
    /// files, asks Windows which window is in the foreground, and sends a normal
    /// simulated key press. It never reads game memory, network traffic or game
    /// files, and key presses are only sent while EFT is the focused window.
    /// </summary>
    internal sealed class AutoScreenshotService : IDisposable
    {
        public const int MinimumIntervalSeconds = 2;
        public const int MaximumIntervalSeconds = 600;
        private const string EftProcessName = "EscapeFromTarkov";

        /// <summary>
        /// Keys that can be chosen for the screenshot binding, as Windows virtual-key codes.
        /// </summary>
        public static readonly IReadOnlyList<KeyValuePair<string, int>> SelectableKeys = BuildSelectableKeys();

        private readonly GameWatcher eft;
        private readonly MessageLog messageLog;
        private readonly System.Timers.Timer timer;
        private readonly object stateLock = new();
        private bool inRaid;
        private bool disposed;

        public AutoScreenshotService(GameWatcher eft, MessageLog messageLog)
        {
            this.eft = eft;
            this.messageLog = messageLog;
            timer = new System.Timers.Timer
            {
                AutoReset = true,
            };
            timer.Elapsed += Timer_Elapsed;

            eft.RaidStarted += Eft_RaidStarted;
            eft.RaidStopping += Eft_RaidOver;
            eft.RaidExited += Eft_RaidOver;
            eft.RaidEnded += Eft_RaidOver;
            eft.GameStopped += Eft_RaidOver;
        }

        public static bool Enabled => Properties.Settings.Default.autoScreenshotEnabled;

        public static int IntervalSeconds => Math.Clamp(
            Properties.Settings.Default.autoScreenshotIntervalSeconds,
            MinimumIntervalSeconds,
            MaximumIntervalSeconds);

        /// <summary>
        /// Re-applies the enabled state and interval from settings. Call after
        /// changing either setting so the change takes effect mid-raid.
        /// </summary>
        public void Restart()
        {
            lock (stateLock)
            {
                timer.Stop();
                if (disposed || !inRaid || !Enabled)
                {
                    return;
                }
                timer.Interval = IntervalSeconds * 1000;
                timer.Start();
            }
        }

        private void Eft_RaidStarted(object? sender, RaidInfoEventArgs e)
        {
            lock (stateLock)
            {
                inRaid = true;
            }
            Restart();
            if (Enabled)
            {
                messageLog.AddMessage($"Automatic screenshots every {IntervalSeconds} seconds while EFT is focused.", "info");
            }
        }

        private void Eft_RaidOver(object? sender, EventArgs e)
        {
            lock (stateLock)
            {
                inRaid = false;
                timer.Stop();
            }
        }

        private void Timer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!Enabled)
            {
                Restart();
                return;
            }
            try
            {
                // Never send the key to another application.
                if (!IsEftForeground())
                {
                    return;
                }
                PressKey((ushort)Properties.Settings.Default.autoScreenshotKey);
            }
            catch (Exception ex)
            {
                lock (stateLock)
                {
                    timer.Stop();
                }
                messageLog.AddMessage($"Automatic screenshots stopped: {ex.Message}", "exception");
            }
        }

        private static bool IsEftForeground()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return false;
            }
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return false;
            }
            try
            {
                using var process = Process.GetProcessById((int)processId);
                return string.Equals(process.ProcessName, EftProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                // The process exited between the two calls.
                return false;
            }
        }

        private static void PressKey(ushort virtualKey)
        {
            // Print Screen's scan code is E0 37. MapVirtualKey can report the
            // Alt+SysRq code (0x54) instead, which EFT treats as a different key.
            var scanCode = virtualKey == VkSnapshot
                ? PrintScreenScanCode
                : (ushort)MapVirtualKey(virtualKey, MapvkVkToVsc);
            uint flags = IsExtendedKey(virtualKey) ? KeyeventfExtendedKey : 0;
            if (scanCode != 0)
            {
                flags |= KeyeventfScanCode;
            }

            var inputs = new[]
            {
                KeyboardInput(virtualKey, scanCode, flags),
                KeyboardInput(virtualKey, scanCode, flags | KeyeventfKeyUp),
            };
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                throw new InvalidOperationException($"Windows rejected the key press (error {Marshal.GetLastWin32Error()}).");
            }
        }

        private static INPUT KeyboardInput(ushort virtualKey, ushort scanCode, uint flags) => new()
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = scanCode,
                    dwFlags = flags,
                },
            },
        };

        private static bool IsExtendedKey(ushort virtualKey) => virtualKey is
            0x21 or 0x22 or 0x23 or 0x24 // PageUp, PageDown, End, Home
            or 0x25 or 0x26 or 0x27 or 0x28 // arrow keys
            or 0x2C or 0x2D or 0x2E // PrintScreen, Insert, Delete
            or 0x6F; // Numpad divide

        private static List<KeyValuePair<string, int>> BuildSelectableKeys()
        {
            var keys = new List<KeyValuePair<string, int>>
            {
                new("Print Screen", 0x2C),
            };
            for (var i = 1; i <= 12; i++)
            {
                keys.Add(new($"F{i}", 0x6F + i));
            }
            keys.Add(new("Insert", 0x2D));
            keys.Add(new("Delete", 0x2E));
            keys.Add(new("Home", 0x24));
            keys.Add(new("End", 0x23));
            keys.Add(new("Page Up", 0x21));
            keys.Add(new("Page Down", 0x22));
            for (var i = 0; i <= 9; i++)
            {
                keys.Add(new($"Numpad {i}", 0x60 + i));
            }
            for (var c = 'A'; c <= 'Z'; c++)
            {
                keys.Add(new(c.ToString(), c));
            }
            for (var c = '0'; c <= '9'; c++)
            {
                keys.Add(new(c.ToString(), c));
            }
            return keys;
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                timer.Stop();
            }
            eft.RaidStarted -= Eft_RaidStarted;
            eft.RaidStopping -= Eft_RaidOver;
            eft.RaidExited -= Eft_RaidOver;
            eft.RaidEnded -= Eft_RaidOver;
            eft.GameStopped -= Eft_RaidOver;
            timer.Dispose();
        }

        private const uint InputKeyboard = 1;
        private const uint KeyeventfExtendedKey = 0x0001;
        private const uint KeyeventfKeyUp = 0x0002;
        private const uint KeyeventfScanCode = 0x0008;
        private const uint MapvkVkToVsc = 0;
        private const ushort VkSnapshot = 0x2C;
        private const ushort PrintScreenScanCode = 0x37;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        // The union must be as large as its biggest member (MOUSEINPUT) for
        // SendInput to accept the structure size.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
