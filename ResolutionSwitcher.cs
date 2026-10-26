using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ResolutionSwitcher
{
    public sealed class DisplayInfo
    {
        public string DeviceName { get; internal set; }
        public string MonitorName { get; internal set; }
        public string MonitorId { get; internal set; }
        public bool IsPrimary { get; internal set; }

        public override string ToString()
        {
            return DeviceName.Replace(@"\\.\", "") + (IsPrimary ? " (primary)" : "") + " - " + MonitorName;
        }
    }

    public sealed class ModeInfo
    {
        public uint Width { get; internal set; }
        public uint Height { get; internal set; }
        public uint RefreshRate { get; internal set; }

        public override string ToString()
        {
            return Width + " x " + Height + " at " + RefreshRate + " Hz";
        }
    }

    public static class DisplayService
    {
        private const uint AttachedToDesktop = 1;
        private const uint PrimaryDevice = 4;
        private const uint MirroringDriver = 8;
        private const uint ModeFields = 0x20 | 0x80 | 0x40000 | 0x80000 | 0x100000 | 0x200000 | 0x400000;
        private const uint TestOnly = 2;

        public static List<DisplayInfo> GetDisplays()
        {
            var displays = new List<DisplayInfo>();
            for (uint index = 0; ; index++)
            {
                var adapter = NewDevice();
                if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;
                if ((adapter.StateFlags & AttachedToDesktop) == 0 || (adapter.StateFlags & MirroringDriver) != 0) continue;

                var monitor = NewDevice();
                string name = adapter.DeviceString;
                string id = "";
                for (uint monitorIndex = 0; EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 1); monitorIndex++)
                {
                    if ((monitor.StateFlags & AttachedToDesktop) != 0)
                    {
                        name = monitor.DeviceString;
                        id = monitor.DeviceID;
                        break;
                    }
                    monitor = NewDevice();
                }
                displays.Add(new DisplayInfo
                {
                    DeviceName = adapter.DeviceName,
                    MonitorName = name,
                    MonitorId = id,
                    IsPrimary = (adapter.StateFlags & PrimaryDevice) != 0
                });
            }
            return displays;
        }

        public static ModeInfo GetCurrentMode(string deviceName)
        {
            return Describe(ReadCurrent(deviceName));
        }

        // This calls CDS_TEST only; it never changes the display.
        public static bool CanUseMode(string deviceName, uint width, uint height, uint refreshRate)
        {
            NativeMode mode;
            return TryFindMode(deviceName, width, height, refreshRate, out mode)
                && ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, TestOnly, IntPtr.Zero) == 0;
        }

        public static string SetResolution(DisplayInfo display, uint width, uint height)
        {
            // Recheck the monitor identity in case screens were unplugged while the app was open.
            bool connected = false;
            foreach (var candidate in GetDisplays())
            {
                if (candidate.DeviceName == display.DeviceName && candidate.MonitorId == display.MonitorId)
                {
                    connected = true;
                    break;
                }
            }
            if (!connected) throw new InvalidOperationException("This monitor is no longer connected. Select a monitor again.");

            const uint refreshRate = 240;
            var original = ReadCurrent(display.DeviceName);
            if (original.dmPelsWidth == width && original.dmPelsHeight == height && original.dmDisplayFrequency == refreshRate)
                return "Already using " + Describe(original) + ".";

            NativeMode target;
            if (!TryFindMode(display.DeviceName, width, height, refreshRate, out target))
                throw new InvalidOperationException(width + " x " + height + " at 240 Hz is not available on this monitor.");

            int result = ChangeDisplaySettingsEx(display.DeviceName, ref target, IntPtr.Zero, TestOnly, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException("Windows rejected this mode: " + Explain(result));

            // A dynamic change keeps the user's saved startup display settings intact.
            result = ChangeDisplaySettingsEx(display.DeviceName, ref target, IntPtr.Zero, 0, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException("Could not change resolution: " + Explain(result));

            var actual = ReadCurrent(display.DeviceName);
            if (actual.dmPelsWidth != width || actual.dmPelsHeight != height || actual.dmDisplayFrequency != refreshRate)
            {
                original.dmFields = ModeFields;
                original.dmDisplayFixedOutput = 0;
                int restored = ChangeDisplaySettingsEx(display.DeviceName, ref original, IntPtr.Zero, 0, IntPtr.Zero);
                throw new InvalidOperationException("Windows applied " + Describe(actual) + " instead. "
                    + (restored == 0 ? "The original mode was restored." : "Restore failed: " + Explain(restored)));
            }
            return "Changed to " + Describe(actual) + ".";
        }

        private static bool TryFindMode(string deviceName, uint width, uint height, uint refreshRate, out NativeMode target)
        {
            var current = ReadCurrent(deviceName);
            for (int index = 0; ; index++)
            {
                var mode = NewMode();
                if (!EnumDisplaySettings(deviceName, index, ref mode)) break;
                if (mode.dmPelsWidth != width || mode.dmPelsHeight != height || mode.dmDisplayFrequency != refreshRate
                    || mode.dmBitsPerPel != 32 || (mode.dmDisplayFlags & 2) != 0
                    || mode.dmDisplayOrientation != current.dmDisplayOrientation) continue;

                mode.dmPositionX = current.dmPositionX;
                mode.dmPositionY = current.dmPositionY;
                mode.dmFields = ModeFields;
                mode.dmDisplayFixedOutput = 0;
                target = mode;
                return true;
            }
            target = new NativeMode();
            return false;
        }

        private static NativeMode ReadCurrent(string deviceName)
        {
            var mode = NewMode();
            if (!EnumDisplaySettings(deviceName, -1, ref mode))
                throw new InvalidOperationException("Windows could not read the selected monitor's current mode.");
            return mode;
        }

        private static ModeInfo Describe(NativeMode mode)
        {
            return new ModeInfo { Width = mode.dmPelsWidth, Height = mode.dmPelsHeight, RefreshRate = mode.dmDisplayFrequency };
        }

        private static string Explain(int code)
        {
            switch (code)
            {
                case 1: return "a restart would be required; no restart was requested.";
                case -1: return "the display driver could not apply the mode.";
                case -2: return "the resolution or refresh rate is unsupported.";
                case -3: return "Windows could not update the display settings.";
                case -4: return "Windows rejected the display flags.";
                case -5: return "Windows rejected the display parameters.";
                case -6: return "the display configuration is unsupported.";
                default: return "Windows error " + code + ".";
            }
        }

        private static NativeDevice NewDevice()
        {
            return new NativeDevice { cb = Marshal.SizeOf(typeof(NativeDevice)) };
        }

        private static NativeMode NewMode()
        {
            return new NativeMode { dmSize = (ushort)Marshal.SizeOf(typeof(NativeMode)) };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(string device, uint index, ref NativeDevice info, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeIndex, ref NativeMode mode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        private static extern int ChangeDisplaySettingsEx(string deviceName, ref NativeMode mode, IntPtr window, uint flags, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
            public uint dmPanningWidth, dmPanningHeight;
        }
    }

    internal sealed class SwitcherForm : Form
    {
        private static readonly Color BackgroundColor = Color.FromArgb(12, 12, 12);
        private static readonly Color SurfaceColor = Color.FromArgb(28, 28, 28);
        private static readonly Color TextColor = Color.FromArgb(224, 224, 224);
        private static readonly Color MutedTextColor = Color.FromArgb(160, 160, 160);
        private readonly ComboBox monitorPicker = new ComboBox();
        private readonly Label currentMode = new Label();
        private readonly Label status = new Label();
        private readonly Button smallButton = new Button();
        private readonly Button desktopButton = new Button();
        private bool refreshing;

        public SwitcherForm()
        {
            Text = "Resolution Switcher";
            Font = new Font("Segoe UI", 10F);
            BackColor = BackgroundColor;
            ForeColor = TextColor;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(460, 240);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = "Choose a monitor and click a resolution.", Dock = DockStyle.Fill }, 0, 0);

            monitorPicker.Dock = DockStyle.Top;
            monitorPicker.DropDownStyle = ComboBoxStyle.DropDownList;
            monitorPicker.DropDownWidth = 650;
            monitorPicker.FlatStyle = FlatStyle.Flat;
            monitorPicker.BackColor = SurfaceColor;
            monitorPicker.ForeColor = TextColor;
            monitorPicker.DrawMode = DrawMode.OwnerDrawFixed;
            monitorPicker.DrawItem += DrawMonitorItem;
            monitorPicker.SelectedIndexChanged += delegate { if (!refreshing) UpdateStatus(); };
            monitorPicker.DropDown += delegate { RefreshDisplays(); };
            layout.Controls.Add(monitorPicker, 0, 1);

            currentMode.Dock = DockStyle.Fill;
            currentMode.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(currentMode, 0, 2);

            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            smallButton.Text = "1280 x 960\r\n240 Hz";
            desktopButton.Text = "1920 x 1080\r\n240 Hz";
            StyleButton(smallButton);
            StyleButton(desktopButton);
            smallButton.Dock = desktopButton.Dock = DockStyle.Fill;
            smallButton.Margin = new Padding(0, 0, 5, 4);
            desktopButton.Margin = new Padding(5, 0, 0, 4);
            smallButton.Click += delegate { Apply(1280, 960); };
            desktopButton.Click += delegate { Apply(1920, 1080); };
            buttons.Controls.Add(smallButton, 0, 0);
            buttons.Controls.Add(desktopButton, 1, 0);
            layout.Controls.Add(buttons, 0, 3);

            status.Dock = DockStyle.Fill;
            status.Font = new Font("Segoe UI", 9F);
            status.ForeColor = MutedTextColor;
            layout.Controls.Add(status, 0, 4);
            Controls.Add(layout);
            Activated += delegate { RefreshDisplays(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int darkMode = 1;
            int captionColor = ColorTranslator.ToWin32(BackgroundColor);
            int captionTextColor = ColorTranslator.ToWin32(TextColor);
            DwmSetWindowAttribute(Handle, 20, ref darkMode, sizeof(int));
            DwmSetWindowAttribute(Handle, 35, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(Handle, 36, ref captionTextColor, sizeof(int));
        }

        private static void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = SurfaceColor;
            button.ForeColor = TextColor;
            button.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 64);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 42, 42);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(56, 56, 56);
        }

        private void DrawMonitorItem(object sender, DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color background = selected ? Color.FromArgb(46, 46, 46) : SurfaceColor;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index >= 0 && e.Index < monitorPicker.Items.Count)
            {
                var bounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 8), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, monitorPicker.Items[e.Index].ToString(), e.Font, bounds,
                    TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            e.DrawFocusRectangle();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private void RefreshDisplays()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var previous = monitorPicker.SelectedItem as DisplayInfo;
                var displays = DisplayService.GetDisplays();
                monitorPicker.Items.Clear();
                int selectedIndex = -1;
                for (int i = 0; i < displays.Count; i++)
                {
                    monitorPicker.Items.Add(displays[i]);
                    if (previous != null && previous.DeviceName == displays[i].DeviceName && previous.MonitorId == displays[i].MonitorId)
                        selectedIndex = i;
                }
                if (selectedIndex < 0)
                {
                    selectedIndex = displays.FindIndex(delegate(DisplayInfo display) { return display.IsPrimary; });
                    if (selectedIndex < 0 && displays.Count > 0) selectedIndex = 0;
                }
                monitorPicker.SelectedIndex = selectedIndex;
                UpdateStatus();
            }
            catch (Exception error) { ShowError(error.Message); }
            finally { refreshing = false; }
        }

        private void UpdateStatus()
        {
            var display = monitorPicker.SelectedItem as DisplayInfo;
            smallButton.Enabled = desktopButton.Enabled = false;
            if (display == null)
            {
                currentMode.Text = "No active monitor found.";
                status.Text = "Connect a monitor, then select it here.";
                return;
            }
            try
            {
                currentMode.Text = "Current: " + DisplayService.GetCurrentMode(display.DeviceName);
                smallButton.Enabled = DisplayService.CanUseMode(display.DeviceName, 1280, 960, 240);
                desktopButton.Enabled = DisplayService.CanUseMode(display.DeviceName, 1920, 1080, 240);
                status.ForeColor = MutedTextColor;
                status.Text = smallButton.Enabled && desktopButton.Enabled ? "Both resolutions are available at 240 Hz."
                    : "A disabled button means that mode is unavailable at 240 Hz.";
            }
            catch (Exception error) { ShowError(error.Message); }
        }

        private void Apply(uint width, uint height)
        {
            var display = monitorPicker.SelectedItem as DisplayInfo;
            if (display == null) return;
            smallButton.Enabled = desktopButton.Enabled = false;
            UseWaitCursor = true;
            try
            {
                string message = DisplayService.SetResolution(display, width, height);
                UpdateStatus();
                status.ForeColor = Color.FromArgb(126, 210, 150);
                status.Text = message;
            }
            catch (Exception error)
            {
                UpdateStatus();
                ShowError(error.Message);
                MessageBox.Show(this, error.Message, "Could not change resolution", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally { UseWaitCursor = false; }
        }

        private void ShowError(string message)
        {
            status.ForeColor = Color.FromArgb(240, 144, 144);
            status.Text = message;
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SwitcherForm());
        }
    }
}
