﻿namespace ZodiacGlass
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Threading;
    using System.Windows;
    using System.Windows.Threading;
    using System.Xml.Serialization;
    using ZodiacGlass.Diagnostics;
    using ZodiacGlass.FFXIV;
    using NotifyIcon = System.Windows.Forms.NotifyIcon;
    using Settings = ZodiacGlass.Properties.Settings;
    using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
    using Update = Updating.Update;
    using Icon = System.Drawing.Icon;
    using Image = System.Drawing.Image;

    public partial class App : Application
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private const string XIVProcessName = "ffxiv";

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly Mutex mutex = new Mutex(true, "ZodiacGlass");

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly Log log = new Log();

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly List<IOverlay> overlays = new List<IOverlay>();

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly ProcessObserver processObserver = new ProcessObserver();

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly NotifyIcon notifyIcon = new NotifyIcon();

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private FFXIVConfig xivConfig;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool isMutexOwner;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private FFXIVMemoryMap currentMemoryMap;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool autoRestart;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            try
            {
                // ensure one one instance of app is open
                if (mutex.WaitOne(TimeSpan.Zero, true))
                {
                    this.isMutexOwner = true;

                    base.OnStartup(e);

                    if (!this.Update())
                    {
                        this.UpgradeSettingsIfNecessary();

                        this.ReadFFXIVConfig();

                        this.LoadMemoryMap();

#if !DEBUG
                        this.TryUpdateMemoryMap();
#endif

                        this.processObserver.ProcessStarted += this.OnProcessStarted;

                        this.ConfigureNotifyIcon();

                        try
                        {
                            this.AttachToExistingProcesses();

                            if (!this.overlays.Any())
                                this.notifyIcon.ShowBalloonTip(2500, "Zodiac Glass started", "Please start FINAL FANTASY XIV: A Realm Reborn to see the overlay.", System.Windows.Forms.ToolTipIcon.Info);
                        }
                        catch (Win32Exception)
                        {
                            this.notifyIcon.ShowBalloonTip(2500, "Attach to FF XIV process failed", "Ensure you're running this program as administrator.", System.Windows.Forms.ToolTipIcon.Error);
                        }
                    }
                    else
                    {
                        this.autoRestart = true;

                        this.Shutdown();
                    }
                }
                else
                {
                    MessageBox.Show("Only one instance of Zodiac Glass be run!", "Zodiac Glass", MessageBoxButton.OK, MessageBoxImage.Warning);

                    this.Shutdown();
                }
            }
            catch (Exception ex)
            {
                this.log.WriteException("Startup failed.", ex);
                throw;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            using (this.processObserver)
            using (this.notifyIcon)
            using (this.mutex)
            using (this.log)
            {
                foreach (IOverlay overlay in this.overlays.ToArray())
                    this.DestroyOverlay(overlay);

                if (this.isMutexOwner)
                    this.mutex.ReleaseMutex();

                base.OnExit(e);

                if (this.autoRestart)
                {
                    Process.Start(Assembly.GetEntryAssembly().Location);
                }
            }
        }

        private void UpgradeSettingsIfNecessary()
        {
            if (!Enum.IsDefined(typeof(OverlayDisplayMode), Settings.Default.OverlayDisplayMode))
            {
                Settings.Default.Upgrade();

                if (!Enum.IsDefined(typeof(OverlayDisplayMode), Settings.Default.OverlayDisplayMode))
                    Settings.Default.OverlayDisplayMode = (int)default(OverlayDisplayMode);
            }
        }

        private void ReadFFXIVConfig()
        {
            try
            {
                using (FFXIVConfigReader reader = new FFXIVConfigReader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"My Games\FINAL FANTASY XIV - A Realm Reborn\FFXIV.cfg")))
                {
                    this.xivConfig = reader.ReadConfig();
                }
            }
            catch (Exception ex)
            {
                this.log.WriteException("Can't read FF XIV config file.", ex);
            }
        }

        private void ConfigureNotifyIcon()
        {
            const string imageRuiFormat = "pack://application:,,,/Zodiac Glass;component/Resources/{0}";

            using (Stream iconStream = Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "ZodiacGlass.ico"))).Stream)
            {
                this.notifyIcon.Icon = new Icon(iconStream);
            }

            this.notifyIcon.Visible = true;

            this.notifyIcon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();

            ToolStripMenuItem newItem;

            newItem = new ToolStripMenuItem("Bounus Light");
            newItem.Image = Image.FromStream(Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "reddit.png"))).Stream);
            newItem.Click += (s, e) => Process.Start("http://www.reddit.com/live/tlfmtjl4fteo");
            this.notifyIcon.ContextMenuStrip.Items.Add(newItem);

            newItem = new ToolStripMenuItem("Light Info");
            newItem.Image = Image.FromStream(Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "reddit.png"))).Stream);
            newItem.Click += (s, e) => Process.Start("http://www.reddit.com/r/ffxiv/comments/2gm1ru/nexus_light_information/");
            this.notifyIcon.ContextMenuStrip.Items.Add(newItem);
            
            this.notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            newItem = new ToolStripMenuItem("Update Memory Addresses");
            newItem.Image = Image.FromStream(Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "update.ico"))).Stream);
            newItem.Click += (s, e) =>
            {
                try
                {
                    if (this.UpdateMemoryMap())
                    {
                        foreach (IOverlay overlay in this.overlays.ToArray())
                        {
                            this.ReCreateOverlay(overlay);
                        }

                        this.notifyIcon.ShowBalloonTip(2500, "Update successfully.", "I hope it works now\r\n\r\n   ( ͡° ͜ʖ ͡°)\r\n", System.Windows.Forms.ToolTipIcon.Info);
                    }
                    else
                        this.notifyIcon.ShowBalloonTip(2500, "Update failed.", "No update available\r\n\r\n   （　´_ゝ`）\r\n", System.Windows.Forms.ToolTipIcon.Warning);
                }
                catch (WebException)
                {
                    this.notifyIcon.ShowBalloonTip(5000, "Update failed.", "Can't connect to update file on github.\r\nPlease check firewall.\r\n\r\n   （　´_ゝ`）\r\n", System.Windows.Forms.ToolTipIcon.Error);
                }
            };
            this.notifyIcon.ContextMenuStrip.Items.Add(newItem);
            
            newItem = new ToolStripMenuItem("Reset Overlay Position");
            newItem.Image = Image.FromStream(Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "reset.ico"))).Stream);
            newItem.Click += (s, e) =>
            {
                switch (this.overlays.Count())
                {
                    case 0:
                        break;
                    case 1:

                        var singleOverlay = this.overlays.FirstOrDefault();

                        singleOverlay.Position = new System.Windows.Point(0, 0);

                        // activate the game window
                        Native.NativeMethods.SetForegroundWindow(singleOverlay.Process.MainWindowHandle);

                        break;
                    default:

                        foreach (OverlayWindow overlay in this.overlays.ToArray())
                        {
                            overlay.Position = new Point(0, 0);
                        }

                        break;
                }
            };
            this.notifyIcon.ContextMenuStrip.Items.Add(newItem);

            this.notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            newItem = new ToolStripMenuItem("Donate");
            newItem.Image = Image.FromStream(Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "donate.ico"))).Stream);
            newItem.Click += (s, e) => Process.Start("https://www.paypal.com/cgi-bin/webscr?hosted_button_id=5MGV57U5FL728&cmd=_s-xclick");
            this.notifyIcon.ContextMenuStrip.Items.Add(newItem);

            newItem = new ToolStripMenuItem("About");
            newItem.Image = Image.FromStream(Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "about.ico"))).Stream);
            string msg;
            using (StreamReader sr = new StreamReader(Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "About.txt"))).Stream))
            {
                msg = sr.ReadToEnd().Replace("{$Copyright}", AssemblyProperties.Copyright);
            }
            newItem.Click += (s, e) => MessageBox.Show(msg, string.Format("About Zodiac Glass v{0}", AssemblyProperties.Version), MessageBoxButton.OK, MessageBoxImage.Information);
            this.notifyIcon.ContextMenuStrip.Items.Add(newItem);

            newItem = new ToolStripMenuItem("Exit");
            newItem.Image = Image.FromStream(Application.GetResourceStream(new Uri(string.Format(imageRuiFormat, "exit.ico"))).Stream);
            newItem.Click += (s, e) => this.Shutdown();
            this.notifyIcon.ContextMenuStrip.Items.Add(newItem);
        }

        private void LoadMemoryMap()
        {
            try
            {
#if DEBUG
                this.currentMemoryMap = FFXIVMemoryMap.Default;
#else
                this.currentMemoryMap = Settings.Default.MemoryMap ?? FFXIVMemoryMap.Default;
#endif
            }
            catch (Exception ex)
            {
                this.log.WriteException("Reading MemoryMap failed.", ex);
                this.currentMemoryMap = FFXIVMemoryMap.Default;
            }
        }

        private bool Update()
        {
            try
            {
                WebRequest request = HttpWebRequest.Create("https://raw.githubusercontent.com/InvisibleShield/FFXIV-Zodiac-Glass/master/UPDATE");

                using (Stream responseStream = request.GetResponse().GetResponseStream())
                {
                    XmlSerializer seri = new XmlSerializer(typeof(Update));

                    Update update = seri.Deserialize(responseStream) as Update;

                    Version serverVersion = Version.Parse(update.Version);
                    Version currentVersion = Version.Parse(AssemblyProperties.Version);

                    if (update != null && serverVersion > currentVersion)
                    {
                        if (MessageBox.Show("Would you like to install the new version?", "Zodiac Glass Update", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        {
                            try
                            {
                                string targetDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

                                MemoryStream[] fileContent = new MemoryStream[update.Content.Length];

                                for (int i = 0; i < update.Content.Length; i++)
                                {
                                    WebRequest fileRequest = HttpWebRequest.Create(update.Content[i].Source);

                                    using (Stream fileResponseStream = fileRequest.GetResponse().GetResponseStream())
                                    {
                                        fileContent[i] = new MemoryStream();

                                        fileResponseStream.CopyTo(fileContent[i]);
                                    }
                                }

                                for (int i = 0; i < update.Content.Length; i++)
                                {
                                    string targetFileName, targetPath;

                                    if (!string.IsNullOrWhiteSpace(update.Content[i].Target))
                                    {
                                        targetFileName = Path.GetFileName(update.Content[i].Target);
                                        targetPath = Path.Combine(targetDir, Path.GetDirectoryName(update.Content[i].Target), targetFileName);
                                    }
                                    else
                                    {
                                        targetFileName = Path.GetFileName(update.Content[i].Source);
                                        targetPath = Path.Combine(targetDir, targetFileName);
                                    }


                                    if (File.Exists(targetPath))
                                    {
                                        string tmpFile = Path.GetTempFileName();

                                        File.Delete(tmpFile);

                                        File.Move(targetPath, tmpFile);
                                    }
                                    else
                                    {
                                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                                    }

                                    using (Stream sourceStream = fileContent[i])
                                    {
                                        sourceStream.Position = 0;

                                        using (Stream targetStream = new FileStream(targetPath, FileMode.CreateNew))
                                        {
                                            sourceStream.CopyTo(targetStream);
                                        }
                                    }
                                }

                                return true;
                            }
                            catch (Exception ex)
                            {
                                this.log.WriteException("Update failed.", ex);
                                MessageBox.Show("The update failed. Please try again later.", "Zodiac Glass Update", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                this.log.WriteException("Update failed.", ex);
                return false;
            }
        }

        private bool TryUpdateMemoryMap()
        {
            try
            {
                return this.UpdateMemoryMap();
            }
            catch (WebException)
            {
                // ignore it, its already logged
                return false;
            }
        }

        private bool UpdateMemoryMap()
        {
            try
            {
                WebRequest request = HttpWebRequest.Create("https://raw.githubusercontent.com/InvisibleShield/FFXIV-Zodiac-Glass/master/MEMMAP");

                using (Stream responseStream = request.GetResponse().GetResponseStream())
                {
                    XmlSerializer seri = new XmlSerializer(typeof(FFXIVMemoryMap));

                    FFXIVMemoryMap newMemMap = seri.Deserialize(responseStream) as FFXIVMemoryMap;

                    if (newMemMap != null && !newMemMap.Equals(this.currentMemoryMap))
                    {
                        this.currentMemoryMap = newMemMap;

                        try
                        {
                            Settings.Default.MemoryMap = newMemMap;
                            Settings.Default.Save();

                            this.log.Write(LogLevel.Info, "MemoryMap updated.");
                        }
                        catch (Exception ex)
                        {
                            this.log.WriteException("Saving new MemoryMap failed.", ex);
                        }

                        return true;
                    }

                    return false;
                }
            }
            catch (WebException ex)
            {
                this.log.WriteException("Updating MemoryMap failed.", ex);
                throw;
            }
            catch (Exception ex)
            {
                this.log.WriteException("Updating MemoryMap failed.", ex);
                return false;
            }
        }

        #region Overlay

        private void AttachToExistingProcesses()
        {
            var processes = Process.GetProcessesByName(App.XIVProcessName).ToArray();

            switch (processes.Count())
            {
                case 0:

                    break;
                case 1:

                    Process singleProcess = processes.First();
                    this.CreateOverlay(singleProcess);

                    // activate the game window
                    Native.NativeMethods.SetForegroundWindow(singleProcess.MainWindowHandle);

                    break;
                default:

                    foreach (Process process in processes)
                        this.CreateOverlay(process);

                    break;
            }

        }

        private void CreateOverlay(Process process)
        {
            this.log.Write(LogLevel.Trace, string.Format("Creating overlay for process {0}.", process.Id));

            try
            {
                IOverlay overlay = new OverlayWindow(process);

                process.EnableRaisingEvents = true;
                process.Exited += this.OnProcessExited;

                overlay.Position = Settings.Default.OverlayPosition;
                overlay.PositionChanged += this.OnOverlayRelativePositionChangedChanged;

                overlay.DisplayMode = (OverlayDisplayMode)Settings.Default.OverlayDisplayMode;
                overlay.MemoryReader = new FFXIVMemoryReader(process, this.currentMemoryMap);

                overlay.DisplayModeChanged += this.OnOverlayDisplayModeChanged;

                overlay.Show();

                this.overlays.Add(overlay);

                this.CheckGameWindowMode(process);
            }
            catch (Win32Exception ex)
            {
                throw ex;
            }
            catch (Exception ex)
            {
                this.log.WriteException("Creating overlay failed.", ex);
            }
        }

        private bool ReCreateOverlay(IOverlay overlay)
        {
            this.DestroyOverlay(overlay);

            if (!overlay.Process.HasExited)
            {
                this.CreateOverlay(overlay.Process);

                return true;
            }

            return false;
        }

        private bool DestroyOverlay(IOverlay overlay)
        {
            if (this.overlays.Contains(overlay))
            {
                overlay.Process.Exited -= this.OnProcessExited;

                overlay.PositionChanged -= this.OnOverlayRelativePositionChangedChanged;
                overlay.DisplayModeChanged -= this.OnOverlayDisplayModeChanged;

                this.log.Write(LogLevel.Trace, string.Format("Destroying overlay for process {0}.", overlay.Process.Id));

                overlay.Close();

                return this.overlays.Remove(overlay);
            }

            return false;
        }

        private void OnOverlayDisplayModeChanged(object sender, ValueChangedEventArgs<OverlayDisplayMode> e)
        {
            OverlayWindow overlay = sender as OverlayWindow;

            if (overlay != null)
            {
                Settings.Default.OverlayDisplayMode = (int)overlay.DisplayMode;
                Settings.Default.Save();
            }
        }

        private void OnOverlayRelativePositionChangedChanged(object sender, EventArgs e)
        {
            OverlayWindow overlay = sender as OverlayWindow;

            if (overlay != null)
            {
                Settings.Default.OverlayPosition = overlay.Position;
                Settings.Default.Save();
            }
        }

        private void OnProcessStarted(object sender, ProcessEventArgs e)
        {
            if (e.Process.ProcessName == App.XIVProcessName)
                dispatcher.Invoke((ThreadStart)(() => this.CreateOverlay(e.Process)));
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            Process process = sender as Process;
            IOverlay overlay = this.overlays.FirstOrDefault(o => o.Process.Id == process.Id);

            if (process != null && overlay != null)
                dispatcher.Invoke((ThreadStart)(() => this.DestroyOverlay(overlay)));
        }

        private void CheckGameWindowMode(Process process)
        {
            FFXIVScreenMode curScreanMode = this.xivConfig.ScreenMode;

            this.log.Write(LogLevel.Info, string.Format("CurrentScreenMode: {0}", curScreanMode));

            if (curScreanMode != FFXIVScreenMode.FramelessWindow && curScreanMode != FFXIVScreenMode.Window)
                MessageBox.Show("FINAL FANTASY XIV have to run into a window mode!", "Window mode required!", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        #endregion

        #region Unhandled Exception

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            this.log.WriteException("Unhandled exception.", e.ExceptionObject as Exception);
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            this.log.WriteException("Unhandled exception.", e.Exception);
        }

        #endregion
    }
}
