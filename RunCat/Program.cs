// Copyright 2025 Timothy Brits
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Resources;
using System.Windows.Forms;
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
            var procMutex = new System.Threading.Mutex(true, "_RUNCAT_MUTEX", out var result);
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
        private const int CPU_TIMER_DEFAULT_INTERVAL = 3000;
        private const int ANIMATE_TIMER_DEFAULT_INTERVAL = 200;
        private readonly PerformanceCounter cpuUsage;
        private readonly ToolStripMenuItem runnerMenu;
        private readonly ToolStripMenuItem themeMenu;
        private readonly ToolStripMenuItem startupMenu;
        private readonly ToolStripMenuItem runnerSpeedLimit;
        private readonly NotifyIcon notifyIcon;
        private string runner = "";
        private int current = 0;
        private float minCPU;
        private float interval;
        private string systemTheme = "";
        private string manualTheme = UserSettings.Default.Theme;
        private string speed = UserSettings.Default.Speed;
        private Icon[] icons;
        private readonly Timer animateTimer = new();
        private readonly Timer cpuTimer = new();

        public RunCatApplicationContext()
        {
            UserSettings.Default.Reload();
            runner = UserSettings.Default.Runner;
            manualTheme = UserSettings.Default.Theme;

            Application.ApplicationExit += OnApplicationExit;
            SystemEvents.UserPreferenceChanged += UserPreferenceChanged;

            cpuUsage = new PerformanceCounter(
                "Processor Information",
                "% Processor Utility",
                "_Total"
            );
            _ = cpuUsage.NextValue(); // Discard first value

            runnerMenu = new ToolStripMenuItem(
                "Runner",
                null,
                new ToolStripMenuItem[]
                {
                    new("Cat", null, SetRunner) { Checked = runner.Equals("cat") },
                    new("Parrot", null, SetRunner) { Checked = runner.Equals("parrot") },
                    new("Horse", null, SetRunner) { Checked = runner.Equals("horse") },
                }
            );

            themeMenu = new ToolStripMenuItem(
                "Theme",
                null,
                new ToolStripMenuItem[]
                {
                    new("Default", null, SetThemeIcons) { Checked = manualTheme.Equals("") },
                    new("Light", null, SetLightIcons) { Checked = manualTheme.Equals("light") },
                    new("Dark", null, SetDarkIcons) { Checked = manualTheme.Equals("dark") },
                }
            );

            startupMenu = new ToolStripMenuItem("Startup", null, SetStartup)
            {
                Checked = IsStartupEnabled(),
            };

            runnerSpeedLimit = new ToolStripMenuItem(
                "Runner Speed Limit",
                null,
                new ToolStripMenuItem[]
                {
                    new("Default", null, SetSpeedLimit) { Checked = speed.Equals("default") },
                    new("CPU 10%", null, SetSpeedLimit) { Checked = speed.Equals("cpu 10%") },
                    new("CPU 20%", null, SetSpeedLimit) { Checked = speed.Equals("cpu 20%") },
                    new("CPU 30%", null, SetSpeedLimit) { Checked = speed.Equals("cpu 30%") },
                    new("CPU 40%", null, SetSpeedLimit) { Checked = speed.Equals("cpu 40%") },
                }
            );

            var contextMenuStrip = new ContextMenuStrip(new Container());
            contextMenuStrip.Items.AddRange(
                new ToolStripItem[]
                {
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
                }
            );

            notifyIcon = new NotifyIcon
            {
                Icon = Resources.light_cat_0,
                ContextMenuStrip = contextMenuStrip,
                Text = "0.0%",
                Visible = true,
            };

            notifyIcon.DoubleClick += HandleDoubleClick;

            UpdateThemeIcons();
            SetAnimation();
            SetSpeed();
            StartObserveCPU();

            current = 1;
        }

        private void OnApplicationExit(object sender, EventArgs e)
        {
            // Save user settings on exit
            UserSettings.Default.Runner = runner;
            UserSettings.Default.Theme = manualTheme;
            UserSettings.Default.Speed = speed;
            UserSettings.Default.Save();
        }

        private bool IsStartupEnabled()
        {
            const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var rKey = Registry.CurrentUser.OpenSubKey(keyName);
            return rKey?.GetValue(Application.ProductName) != null;
        }

        private string GetAppsUseTheme()
        {
            const string keyName = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using var rKey = Registry.CurrentUser.OpenSubKey(keyName);

            if (rKey == null || rKey.GetValue("SystemUsesLightTheme") == null)
            {
                // Default to light if theme cannot be determined
                return "light";
            }

            int theme = (int)rKey.GetValue("SystemUsesLightTheme");
            return theme == 0 ? "dark" : "light";
        }

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
                list.Add((Icon)rm.GetObject($"{prefix}_{runner}_{i}"));
            }

            icons = [.. list];
        }

        private void UpdateCheckedState(ToolStripMenuItem sender, ToolStripMenuItem menu)
        {
            foreach (ToolStripMenuItem item in menu.DropDownItems)
            {
                item.Checked = false;
            }
            sender.Checked = true;
        }

        private void SetRunner(object sender, EventArgs e)
        {
            var item = (ToolStripMenuItem)sender;
            UpdateCheckedState(item, runnerMenu);
            runner = item.Text.ToLower();
            SetIcons();
        }

        private void SetThemeIcons(object sender, EventArgs e)
        {
            UpdateCheckedState((ToolStripMenuItem)sender, themeMenu);
            manualTheme = "";
            systemTheme = GetAppsUseTheme();
            SetIcons();
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

        private void SetSpeedLimit(object sender, EventArgs e)
        {
            var item = (ToolStripMenuItem)sender;
            UpdateCheckedState(item, runnerSpeedLimit);
            speed = item.Text.ToLower();
            SetSpeed();
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
            if (!systemTheme.Equals(newTheme))
            {
                systemTheme = newTheme;
                SetIcons();
            }
        }

        private void SetLightIcons(object sender, EventArgs e)
        {
            UpdateCheckedState((ToolStripMenuItem)sender, themeMenu);
            manualTheme = "light";
            SetIcons();
        }

        private void SetDarkIcons(object sender, EventArgs e)
        {
            UpdateCheckedState((ToolStripMenuItem)sender, themeMenu);
            manualTheme = "dark";
            SetIcons();
        }

        private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                UpdateThemeIcons();
            }
        }

        private void SetStartup(object sender, EventArgs e)
        {
            startupMenu.Checked = !startupMenu.Checked;
            const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var rKey = Registry.CurrentUser.OpenSubKey(keyName, true);

            if (startupMenu.Checked)
            {
                rKey.SetValue(Application.ProductName, Environment.ProcessPath);
            }
            else
            {
                rKey.DeleteValue(Application.ProductName, false);
            }
        }

        private void Exit(object sender, EventArgs e)
        {
            cpuUsage.Close();
            animateTimer.Stop();
            cpuTimer.Stop();
            notifyIcon.Visible = false;
            Application.Exit();
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            if (icons.Length <= current)
            {
                current = 0;
            }

            notifyIcon.Icon = icons[current];
            current = (current + 1) % icons.Length;
        }

        private void SetAnimation()
        {
            animateTimer.Interval = ANIMATE_TIMER_DEFAULT_INTERVAL;
            animateTimer.Tick += AnimationTick;
        }

        private void CPUTickSpeed()
        {
            // Adjust animation speed based on CPU usage or manual setting
            float intervalToUse = speed.Equals("default") ? interval : Math.Max(minCPU, interval);
            animateTimer.Stop();
            animateTimer.Interval = (int)intervalToUse;
            animateTimer.Start();
        }

        private void CPUTick()
        {
            // Update CPU usage and animation interval
            interval = Math.Min(100, cpuUsage.NextValue());
            notifyIcon.Text = $"{interval:f1}%";
            interval = 200.0f / Math.Max(1.0f, Math.Min(20.0f, interval / 5.0f));

            CPUTickSpeed();
        }

        private void ObserveCPUTick(object sender, EventArgs e)
        {
            CPUTick();
        }

        private void StartObserveCPU()
        {
            cpuTimer.Interval = CPU_TIMER_DEFAULT_INTERVAL;
            cpuTimer.Tick += ObserveCPUTick;
            cpuTimer.Start();
        }

        private void HandleDoubleClick(object sender, EventArgs e)
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
    }
}
