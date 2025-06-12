// Copyright 2025 Timothy Brits
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.ComponentModel;
using System.Diagnostics;
using System.Resources;
using Microsoft.Win32;
using RunCat.Properties;

namespace RunCat
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Ensure only one instance is running
            var procMutex = new Mutex(true, "_RUNCAT_MUTEX", out var result);
            if (!result)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.Run(new RunCatApplicationContext());

            procMutex.ReleaseMutex();
        }
    }

    public class RunCatApplicationContext : ApplicationContext
    {
        // --- Constants ---
        private const int CPU_TIMER_DEFAULT_INTERVAL = 1000;
        private const int ANIMATE_TIMER_DEFAULT_INTERVAL = 200;

        // --- Fields ---
        private readonly PerformanceCounter cpuUsage;
        private readonly ToolStripMenuItem runnerMenu;
        private readonly ToolStripMenuItem themeMenu;
        private readonly ToolStripMenuItem startupMenu;
        private readonly ToolStripMenuItem runnerSpeedLimit;
        private readonly NotifyIcon notifyIcon;
        private readonly System.Windows.Forms.Timer animateTimer = new();
        private readonly System.Windows.Forms.Timer cpuTimer = new();
        private string runner = "";
        private int current = 0;
        private float minCPU;
        private float interval;
        private string systemTheme = "";
        private string manualTheme = UserSettings.Default.Theme;
        private string speed = UserSettings.Default.Speed;
        private Icon[] icons = [];

        // --- Constructor ---
        public RunCatApplicationContext()
        {
            // Load user settings
            UserSettings.Default.Reload();
            runner = UserSettings.Default.Runner;
            manualTheme = UserSettings.Default.Theme;

            // Register event handlers
            Application.ApplicationExit += OnApplicationExit;
            SystemEvents.UserPreferenceChanged += UserPreferenceChanged;

            // Initialize CPU usage counter
            cpuUsage = new PerformanceCounter(
                "Processor Information",
                "% Processor Utility",
                "_Total"
            );
            _ = cpuUsage.NextValue(); // Discard first value

            // Build context menu
            runnerMenu = new ToolStripMenuItem(
                "Runner",
                null,
                [
                    new ToolStripMenuItem("Cat", null, SetRunner)
                    {
                        Checked = runner.Equals("cat", StringComparison.Ordinal),
                    },
                    new ToolStripMenuItem("Parrot", null, SetRunner)
                    {
                        Checked = runner.Equals("parrot", StringComparison.Ordinal),
                    },
                    new ToolStripMenuItem("Horse", null, SetRunner)
                    {
                        Checked = runner.Equals("horse", StringComparison.Ordinal),
                    },
                ]
            );

            themeMenu = new ToolStripMenuItem(
                "Theme",
                null,
                [
                    new ToolStripMenuItem("Default", null, SetThemeIcons)
                    {
                        Checked = manualTheme.Equals("", StringComparison.Ordinal),
                    },
                    new ToolStripMenuItem("Light", null, SetLightIcons)
                    {
                        Checked = manualTheme.Equals("light", StringComparison.Ordinal),
                    },
                    new ToolStripMenuItem("Dark", null, SetDarkIcons)
                    {
                        Checked = manualTheme.Equals("dark", StringComparison.Ordinal),
                    },
                ]
            );

            startupMenu = new ToolStripMenuItem("Startup", null, SetStartup)
            {
                Checked = IsStartupEnabled(),
            };

            runnerSpeedLimit = new ToolStripMenuItem(
                "Runner Speed Limit",
                null,
                [
                    new ToolStripMenuItem("Default", null, SetSpeedLimit)
                    {
                        Checked = speed.Equals("default", StringComparison.Ordinal),
                    },
                    new ToolStripMenuItem("CPU 10%", null, SetSpeedLimit)
                    {
                        Checked = speed.Equals("cpu 10%", StringComparison.Ordinal),
                    },
                    new ToolStripMenuItem("CPU 20%", null, SetSpeedLimit)
                    {
                        Checked = speed.Equals("cpu 20%", StringComparison.Ordinal),
                    },
                    new ToolStripMenuItem("CPU 30%", null, SetSpeedLimit)
                    {
                        Checked = speed.Equals("cpu 30%", StringComparison.Ordinal),
                    },
                    new ToolStripMenuItem("CPU 40%", null, SetSpeedLimit)
                    {
                        Checked = speed.Equals("cpu 40%", StringComparison.Ordinal),
                    },
                ]
            );

            var contextMenuStrip = new ContextMenuStrip(new Container());
            contextMenuStrip.Items.AddRange(
                [
                    runnerMenu,
                    themeMenu,
                    startupMenu,
                    runnerSpeedLimit,
                    new ToolStripSeparator(),
                    new ToolStripMenuItem(
                        $"{Application.ProductName} v{Application.ProductVersion}"
                    )
                    {
                        Enabled = false,
                    },
                    new ToolStripMenuItem("Exit", null, Exit),
                ]
            );

            // Setup notify icon
            notifyIcon = new NotifyIcon
            {
                Icon = Resources.light_cat_0,
                ContextMenuStrip = contextMenuStrip,
                Text = "0.0%",
                Visible = true,
            };
            notifyIcon.DoubleClick += HandleDoubleClick;

            // Initialize animation and CPU monitoring
            UpdateThemeIcons();
            SetAnimation();
            SetSpeed();
            StartObserveCPU();

            current = 1;
        }

        // --- Static helpers ---
        private static bool IsStartupEnabled()
        {
            const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var rKey = Registry.CurrentUser.OpenSubKey(keyName);
            return rKey?.GetValue(Application.ProductName) != null;
        }

        private static string GetAppsUseTheme()
        {
            const string keyName = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using var rKey = Registry.CurrentUser.OpenSubKey(keyName);

            if (rKey == null || rKey.GetValue("SystemUsesLightTheme") == null)
            {
                return "light"; // Default to light if theme cannot be determined
            }

            object? themeObj = rKey.GetValue("SystemUsesLightTheme");
            int theme = themeObj is int t ? t : 1;
            return theme == 0 ? "dark" : "light";
        }

        private static void UpdateCheckedState(ToolStripMenuItem sender, ToolStripMenuItem menu)
        {
            foreach (ToolStripMenuItem item in menu.DropDownItems)
            {
                item.Checked = false;
            }
            sender.Checked = true;
        }

        // --- Event handlers ---
        private void OnApplicationExit(object? sender, EventArgs e)
        {
            // Save user settings on exit
            UserSettings.Default.Runner = runner;
            UserSettings.Default.Theme = manualTheme;
            UserSettings.Default.Speed = speed;
            UserSettings.Default.Save();
        }

        private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                UpdateThemeIcons();
            }
        }

        private void SetRunner(object? sender, EventArgs? e)
        {
            var item = (ToolStripMenuItem)sender!;
            UpdateCheckedState(item, runnerMenu);
            runner = item.Text != null ? item.Text.ToLower() : string.Empty;
            SetIcons();
        }

        private void SetThemeIcons(object? sender, EventArgs e)
        {
            UpdateCheckedState((ToolStripMenuItem)sender!, themeMenu);
            manualTheme = "";
            systemTheme = GetAppsUseTheme();
            SetIcons();
        }

        private void SetLightIcons(object? sender, EventArgs e)
        {
            UpdateCheckedState((ToolStripMenuItem)sender!, themeMenu);
            manualTheme = "light";
            SetIcons();
        }

        private void SetDarkIcons(object? sender, EventArgs e)
        {
            UpdateCheckedState((ToolStripMenuItem)sender!, themeMenu);
            manualTheme = "dark";
            SetIcons();
        }

        private void SetStartup(object? sender, EventArgs? e)
        {
            startupMenu.Checked = !startupMenu.Checked;
            const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var rKey = Registry.CurrentUser.OpenSubKey(keyName, true);

            if (startupMenu.Checked)
            {
                if (!string.IsNullOrEmpty(Environment.ProcessPath))
                {
                    rKey?.SetValue(Application.ProductName, Environment.ProcessPath);
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(Application.ProductName))
                {
                    rKey?.DeleteValue(Application.ProductName, false);
                }
            }
        }

        private void SetSpeedLimit(object? sender, EventArgs e)
        {
            var item = (ToolStripMenuItem)sender!;
            UpdateCheckedState(item, runnerSpeedLimit);
            speed = item.Text != null ? item.Text.ToLower() : string.Empty;
            SetSpeed();
        }

        private void Exit(object? sender, EventArgs? e)
        {
            cpuUsage.Close();
            animateTimer.Stop();
            cpuTimer.Stop();
            notifyIcon.Visible = false;
            Application.Exit();
        }

        private void HandleDoubleClick(object? sender, EventArgs e)
        {
            // Open Task Manager on double-click
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                Arguments = " -c Start-Process taskmgr.exe",
                CreateNoWindow = true,
            };
            Process.Start(startInfo);
        }

        private void AnimationTick(object? sender, EventArgs e)
        {
            if (icons.Length <= current)
            {
                current = 0;
            }

            notifyIcon.Icon = icons[current];
            current = (current + 1) % icons.Length;
        }

        private void ObserveCPUTick(object? sender, EventArgs e)
        {
            CPUTick();
        }

        // --- Private helpers ---
        private void SetIcons()
        {
            // Set icons based on runner and theme
            string prefix = manualTheme.Length > 0 ? manualTheme : systemTheme;
            ResourceManager rm = Resources.ResourceManager;

            int capacity = runner switch
            {
                "parrot" => 10,
                "horse" => 14,
                _ => 5,
            };

            var list = new List<Icon>(capacity);
            for (int i = 0; i < capacity; i++)
            {
                object? iconObj = rm.GetObject($"{prefix}_{runner}_{i}");
                if (iconObj is Icon icon)
                {
                    list.Add(icon);
                }
            }
            icons = [.. list];
        }

        private void UpdateThemeIcons()
        {
            // Update icons if theme has changed
            if (manualTheme.Length > 0)
            {
                SetIcons();
                return;
            }
            string newTheme = GetAppsUseTheme();
            if (!systemTheme.Equals(newTheme, StringComparison.Ordinal))
            {
                systemTheme = newTheme;
                SetIcons();
            }
        }

        private void SetAnimation()
        {
            animateTimer.Interval = ANIMATE_TIMER_DEFAULT_INTERVAL;
            animateTimer.Tick += AnimationTick;
        }

        private void SetSpeed()
        {
            // Set minimum CPU interval based on speed setting
            minCPU = speed switch
            {
                "cpu 10%" => 100f,
                "cpu 20%" => 50f,
                "cpu 30%" => 33f,
                "cpu 40%" => 25f,
                _ => minCPU,
            };
        }

        private void StartObserveCPU()
        {
            cpuTimer.Interval = CPU_TIMER_DEFAULT_INTERVAL;
            cpuTimer.Tick += ObserveCPUTick;
            cpuTimer.Start();
        }

        private void CPUTick()
        {
            // Update CPU usage and animation interval
            interval = Math.Min(100, cpuUsage.NextValue());
            notifyIcon.Text = $"{interval:f1}%";
            interval = 200.0f / Math.Max(1.0f, Math.Min(20.0f, interval / 5.0f));
            CPUTickSpeed();
        }

        private void CPUTickSpeed()
        {
            // Adjust animation speed based on CPU usage or manual setting
            float intervalToUse = speed.Equals("default", StringComparison.Ordinal)
                ? interval
                : Math.Max(minCPU, interval);
            animateTimer.Stop();
            animateTimer.Interval = (int)intervalToUse;
            animateTimer.Start();
        }
    }
}
