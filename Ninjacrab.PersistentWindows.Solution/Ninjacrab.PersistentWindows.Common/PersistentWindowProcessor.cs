using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

using LiteDB;
using ManagedWinapi.Windows;

using Ninjacrab.PersistentWindows.Common.Diagnostics;
using Ninjacrab.PersistentWindows.Common.Models;
using Ninjacrab.PersistentWindows.Common.WinApiBridge;

namespace Ninjacrab.PersistentWindows.Common
{
    public class PersistentWindowProcessor : IDisposable
    {
        // constant
        private const int RestoreLatency = 500; // default delay in milliseconds from display change to window restore
        private const int SlowRestoreLatency = 2000; // delay in milliseconds from power resume to window restore
        private const int MaxRestoreLatency = 5000; // max delay in milliseconds from final restore pass to restore finish
        private const int MinRestoreTimes = 2; // minimum restore passes
        private const int MaxRestoreTimesLocal = 4; // max restore passes for local console session
        private const int MaxRestoreTimesRemote = 8; // max restore passes for remote desktop session

        private const int CaptureLatency = 3000; // delay in milliseconds from window move to capture
        private const int MinCaptureToRestoreLatency = 2 * CaptureLatency + 500; // delay in milliseconds from last capture to start restore
        private const int MaxUserMoves = 4; // max user window moves per capture cycle
        private const int MinWindowOsMoveEvents = 12; // threshold of window move events initiated by OS per capture cycle
        private const int MaxHistoryQueueLength = 10;

        private const int HideIconLatency = 2000; // delay in millliseconds from restore finished to hide icon

        private bool initialized = false;

        // window position database
        private Dictionary<string, Dictionary<IntPtr, List<ApplicationDisplayMetrics>>> monitorApplications
            = new Dictionary<string, Dictionary<IntPtr, List<ApplicationDisplayMetrics>>>(); //in-memory database
        private LiteDatabase persistDB; //on-disk database
        private Dictionary<string, POINT> lastCursorPos = new Dictionary<string, POINT>();

        // control shared by capture and restore
        private Object databaseLock = new Object(); // lock access to window position database
        private Object controlLock = new Object();

        // capture control
        private Timer captureTimer;
        private string curDisplayKey = null; // current display config name
        private Dictionary<IntPtr, string> windowTitle = new Dictionary<IntPtr, string>(); // for matching running window with DB record
        private Queue<IntPtr> pendingCaptureWindows = new Queue<IntPtr>(); // queue of window with possible position change for capture
        public Dictionary<uint, string> processCmd = new Dictionary<uint, string>();

        // restore control
        private Timer restoreTimer;
        private Timer restoreFinishedTimer;
        public bool restoringFromMem = false; // automatic restore from memory in progress
        public bool restoringFromDB = false; // manual restore from DB in progress
        public bool dryRun = false; // only capturre, no actual restore
        public bool fixZorder = false; // restore z-order
        public bool pauseAutoRestore = false;
        public bool redrawDesktop = false;
        private int restoreTimes = 0;
        private int restoreHaltTimes = 0; // halt restore due to unstable display setting change
        private int restoreNestLevel = 0; // nested restore call level
        private HashSet<IntPtr> restoredWindows = new HashSet<IntPtr>();
        private Dictionary<int, bool> dbMatchWindow = new Dictionary<int, bool>(); // db entry (id) matches existing window
        private Dictionary<string, int> multiwindowProcess = new Dictionary<string, int>()
            {
                // avoid launch process multiple times
                { "chrome", 0},
                { "firefox", 0 },
                { "opera", 0},
            };

        private Dictionary<string, string> realProcessFileName = new Dictionary<string, string>()
            {
                { "WindowsTerminal.exe", "wt.exe"},
            };

        private string appDataFolder;

        // session control
        private bool remoteSession = false;
        private bool sessionLocked = false; //requires password to unlock
        public bool sessionActive = true;

        // display session end time
        private Dictionary<string, DateTime> lastUserActionTime = new Dictionary<string, DateTime>();

        private bool iconActive = false;
        private Timer hideIconTimer;

        // callbacks
        public delegate void CallBack();
        public CallBack showRestoreTip;
        public CallBack hideRestoreTip;

        public delegate void CallBackBool(bool en);
        public CallBackBool enableRestoreMenu;

        private PowerModeChangedEventHandler powerModeChangedHandler;
        private EventHandler displaySettingsChangingHandler;
        private EventHandler displaySettingsChangedHandler;
        private SessionSwitchEventHandler sessionSwitchEventHandler;

        private readonly List<IntPtr> winEventHooks = new List<IntPtr>();
        private User32.WinEventDelegate winEventsCaptureDelegate;

#if DEBUG
        private void DebugInterval()
        {
            ;
        }
#endif
        public bool Start()
        {
            string productName = System.Windows.Forms.Application.ProductName;
            appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), productName);

#if DEBUG
            appDataFolder = "."; //avoid db path conflict with release version
#endif
            // remove outdated db files
            var dir = Directory.CreateDirectory(appDataFolder);
            var db_version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            foreach (var file in dir.EnumerateFiles($@"{productName}*.db"))
            {
                var fname = file.Name;
                if (!fname.Contains(db_version))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex.ToString());
                    }
                }
            }

            try
            {
                persistDB = new LiteDatabase($@"{appDataFolder}/{productName}.{db_version}.db");
            }
            catch (Exception)
            {
                System.Windows.Forms.MessageBox.Show($"Only one instance of {productName} can be run!");
                return false;
            }

            curDisplayKey = GetDisplayKey();
            enableRestoreMenu(persistDB.CollectionExists(curDisplayKey));
            CaptureNewDisplayConfig(curDisplayKey);

#if DEBUG
            //TestSetWindowPos();

            var debugTimer = new Timer(state =>
            {
                DebugInterval();
            });
            debugTimer.Change(2000, 2000);
#endif            

            captureTimer = new Timer(state =>
            {
                if (!sessionActive)
                {
                    return;
                }

                Log.Trace("Capture timer expired");
                BatchCaptureApplicationsOnCurrentDisplays();
            });

            restoreTimer = new Timer(state =>
            {
                if (pauseAutoRestore && !restoringFromDB)
                    return;

                Log.Trace("Restore timer expired");
                BatchRestoreApplicationsOnCurrentDisplays();
            });
            
            restoreFinishedTimer = new Timer(state =>
            {
                // clear DbMatchWindow flag in db
                if (restoringFromDB)
                {
                    dbMatchWindow.Clear();
                }

                int numWindowRestored = restoredWindows.Count;

                restoringFromDB = false;
                restoringFromMem = false;
                ResetState();
                Log.Trace("");
                Log.Trace("");
                string displayKey = GetDisplayKey();
                if (!displayKey.Equals(curDisplayKey))
                {
                    Log.Error("Restore aborted for {0}", curDisplayKey);

                    // do restore again, while keeping previous capture time unchanged
                    curDisplayKey = displayKey;
                    Log.Event("Restart restore for {0}", curDisplayKey);
                    restoringFromMem = true;
                    StartRestoreTimer();
                }
                else
                {
                    if (redrawDesktop)
                        User32.RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, User32.RedrawWindowFlags.Invalidate);
                    Log.Event("Restore finished with {0} windows recovered for display setting {1}", numWindowRestored, curDisplayKey);
                    sessionActive = true;
                    enableRestoreMenu(persistDB.CollectionExists(curDisplayKey));
                    //RemoveBatchCaptureTime();

                    hideIconTimer.Change(HideIconLatency, Timeout.Infinite);
                }

            });

            hideIconTimer = new Timer(stage =>
            {
                hideRestoreTip();
                iconActive = false;
            });

            winEventsCaptureDelegate = WinEventProc;

            // captures new window, user click, snap and minimize
            this.winEventHooks.Add(User32.SetWinEventHook(
                User32Events.EVENT_SYSTEM_FOREGROUND,
                User32Events.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                winEventsCaptureDelegate,
                0,
                0,
                (uint)User32Events.WINEVENT_OUTOFCONTEXT));

            // captures user dragging
            this.winEventHooks.Add(User32.SetWinEventHook(
                User32Events.EVENT_SYSTEM_MOVESIZEEND,
                User32Events.EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero,
                winEventsCaptureDelegate,
                0,
                0,
                (uint)User32Events.WINEVENT_OUTOFCONTEXT));

            // captures user restore window
            this.winEventHooks.Add(User32.SetWinEventHook(
                User32Events.EVENT_SYSTEM_MINIMIZESTART,
                User32Events.EVENT_SYSTEM_MINIMIZEEND, //window restored
                IntPtr.Zero,
                winEventsCaptureDelegate,
                0,
                0,
                (uint)User32Events.WINEVENT_OUTOFCONTEXT));

            // capture both system and user move action
            this.winEventHooks.Add(User32.SetWinEventHook(
                User32Events.EVENT_OBJECT_LOCATIONCHANGE,
                User32Events.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                winEventsCaptureDelegate,
                0,
                0,
                (uint)User32Events.WINEVENT_OUTOFCONTEXT));

            // capture window close
            this.winEventHooks.Add(User32.SetWinEventHook(
                User32Events.EVENT_OBJECT_DESTROY,
                User32Events.EVENT_OBJECT_DESTROY,
                IntPtr.Zero,
                winEventsCaptureDelegate,
                0,
                0,
                (uint)User32Events.WINEVENT_OUTOFCONTEXT));

            this.displaySettingsChangingHandler =
                (s, e) =>
                {
                    string displayKey = GetDisplayKey();
                    Log.Trace("");
                    Log.Info("Display settings changing {0}", displayKey);
                    lock (controlLock)
                    {
                        if (!restoringFromMem)
                        {
                            EndDisplaySession();
                        }
                    }
                };

            SystemEvents.DisplaySettingsChanging += this.displaySettingsChangingHandler;

            this.displaySettingsChangedHandler =
                (s, e) =>
                {
                    string displayKey = GetDisplayKey();
                    Log.Info("Display settings changed {0}", displayKey);

                    lock (controlLock)
                    {
                        if (sessionLocked)
                        {
                            EndDisplaySession();
                            curDisplayKey = displayKey;
                            //wait for session unlock to start restore
                        }
                        else if (restoringFromMem)
                        {
                            if (!displayKey.Equals(curDisplayKey))
                            {
                                Log.Event("Restore halted due to new display setting change {0}", displayKey);
                            }
                        }
                        else
                        {
                            EndDisplaySession();
                            // change display on the fly
                            ResetState();
                            restoringFromMem = true;
                            curDisplayKey = displayKey;
                            StartRestoreTimer();
                        }
                    }
                };

            SystemEvents.DisplaySettingsChanged += this.displaySettingsChangedHandler;

            powerModeChangedHandler =
                (s, e) =>
                {
                    switch (e.Mode)
                    {
                        case PowerModes.Suspend:
                            Log.Event("System suspending");
                            lock (controlLock)
                            {
                                sessionActive = false;
                                if (!sessionLocked)
                                {
                                    EndDisplaySession();
                                }
                            }
                            break;

                        case PowerModes.Resume:
                            Log.Event("System Resuming");
                            lock (controlLock)
                            {
                                if (!sessionLocked)
                                {
                                    // force restore in case OS does not generate display changed event
                                    restoringFromMem = true;
                                    StartRestoreTimer(milliSecond: SlowRestoreLatency);
                                }
                            }
                            break;
                    }
                };

            SystemEvents.PowerModeChanged += powerModeChangedHandler;

            sessionSwitchEventHandler = (sender, args) =>
            {
                switch (args.Reason)
                {
                    case SessionSwitchReason.SessionLock:
                        Log.Event("Session closing: reason {0}", args.Reason);
                        lock (controlLock)
                        {
                            sessionLocked = true;
                            sessionActive = false;
                            EndDisplaySession();
                        }
                        break;
                    case SessionSwitchReason.SessionUnlock:
                        Log.Event("Session opening: reason {0}", args.Reason);
                        lock (controlLock)
                        {
                            sessionLocked = false;
                            // force restore in case OS does not generate display changed event
                            restoringFromMem = true;
                            StartRestoreTimer();
                        }
                        break;

                    case SessionSwitchReason.RemoteDisconnect:
                    case SessionSwitchReason.ConsoleDisconnect:
                        sessionActive = false;
                        Log.Trace("Session closing: reason {0}", args.Reason);
                        break;

                    case SessionSwitchReason.RemoteConnect:
                        Log.Trace("Session opening: reason {0}", args.Reason);
                        remoteSession = true;
                        break;
                    case SessionSwitchReason.ConsoleConnect:
                        remoteSession = false;
                        Log.Trace("Session opening: reason {0}", args.Reason);
                        break;
                }
            };

            SystemEvents.SessionSwitch += sessionSwitchEventHandler;

            initialized = true;
            return true;
        }

        private bool IsFullScreen(IntPtr hwnd)
        {
            long style = User32.GetWindowLong(hwnd, User32.GWL_STYLE);
            bool isFullScreen = false;
            if ((style & (long)WindowStyleFlags.MAXIMIZEBOX) == 0L)
            {
                RECT2 screenPosition = new RECT2();
                User32.GetWindowRect(hwnd, ref screenPosition);

                string size = string.Format("Res{0}x{1}", screenPosition.Width, screenPosition.Height);
                if (curDisplayKey.Contains(size))
                    isFullScreen = true;
            }

            return isFullScreen;
        }

        private string GetWindowTitle(IntPtr hwnd)
        {
            return windowTitle.ContainsKey(hwnd) ? windowTitle[hwnd] : hwnd.ToString("X8");
        }

        private bool IsMinimized(IntPtr hwnd)
        {
            bool result = false;
            long style = User32.GetWindowLong(hwnd, User32.GWL_STYLE);
            if ((style & (long)WindowStyleFlags.MINIMIZE) != 0L)
            {
                result = true;
            }

            return result;
        }

        private bool IsOffScreen(IntPtr hwnd)
        {
            const int MinSize = 20;
            RECT2 rect = new RECT2();
            User32.GetWindowRect(hwnd, ref rect);
            if (rect.Width <= MinSize || rect.Height <= MinSize)
                return false;

            POINT topLeft = new POINT(rect.Left + MinSize, rect.Top + MinSize);
            if (User32.MonitorFromPoint(topLeft, User32.MONITOR_DEFAULTTONULL) == IntPtr.Zero)
            {
                Log.Error("top left of Rect {0} is off-screen", rect.ToString());
                return true;
            }

            POINT topRight = new POINT(rect.Right - MinSize, rect.Top + MinSize);
            if (User32.MonitorFromPoint(topRight, User32.MONITOR_DEFAULTTONULL) == IntPtr.Zero)
            {
                Log.Error("top right of Rect {0} is off-screen", rect.ToString());
                return true;
            }

            return false;
        }

        private void ActivateWindow(IntPtr hwnd)
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(500);
                lock (databaseLock)
                {
                    if (!monitorApplications[curDisplayKey].ContainsKey(hwnd))
                    {
                        bool isNewWindow = true;
                        foreach (var key in monitorApplications.Keys)
                        {
                            if (monitorApplications[key].ContainsKey(hwnd))
                            {
                                isNewWindow = false;
                                break;
                            }
                        }

                        if (isNewWindow && !IsMinimized(hwnd) && IsOffScreen(hwnd))
                        {
                            IntPtr desktopWindow = User32.GetDesktopWindow();
                            RECT2 rect = new RECT2();
                            User32.GetWindowRect(desktopWindow, ref rect);
                            //User32.MoveWindow(hwnd, 200, 200, 400, 300, true);
                            User32.MoveWindow(hwnd, rect.Left + 200, rect.Top + 200, 400, 300, true);
                            Log.Error("Auto fix invisible window \"{0}\"", GetWindowTitle(hwnd));
                        }
                        return;
                    }

                    if (IsMinimized(hwnd))
                        return; // already minimized

                    if (monitorApplications[curDisplayKey][hwnd].Count == 0)
                        return;

                    var prevDisplayMetrics = monitorApplications[curDisplayKey][hwnd].Last();
                    if (prevDisplayMetrics.IsMinimized)
                    {
                        if (prevDisplayMetrics.IsFullScreen)
                            RestoreFullScreenWindow(hwnd); //the window was minimized from full screen status
                        else if (!IsFullScreen(hwnd))
                        {
                            RECT2 screenPosition = new RECT2();
                            User32.GetWindowRect(hwnd, ref screenPosition);

                            var count = monitorApplications[curDisplayKey][hwnd].Count;
                            for (var i = count - 2; i >= 0; --i)
                            {
                                // restore to position prior to minimize
                                var prev = monitorApplications[curDisplayKey][hwnd][i];
                                if (prev.IsMinimized)
                                    continue;

                                RECT2 rect = prev.ScreenPosition;
                                if (rect.Left <= -25600)
                                {
                                    Log.Error("no qualified position data to restore minimized window \"{0}\"", GetWindowTitle(hwnd));
                                    continue;
                                }

                                if (!screenPosition.Equals(rect))
                                {
                                    // windows ignores previous snap status when activated from minimized state
                                    var placement = prev.WindowPlacement;
                                    User32.SetWindowPlacement(hwnd, ref placement);
                                    User32.MoveWindow(hwnd, rect.Left, rect.Top, rect.Width, rect.Height, true);
                                    Log.Error("restore minimized window \"{0}\"", GetWindowTitle(hwnd));
                                }
                                break;
                            }

                            if (IsOffScreen(hwnd))
                            {
                                IntPtr desktopWindow = User32.GetDesktopWindow();
                                RECT2 rect = new RECT2();
                                User32.GetWindowRect(desktopWindow, ref rect);
                                //User32.MoveWindow(hwnd, 200, 200, 400, 300, true);
                                User32.MoveWindow(hwnd, rect.Left + 200, rect.Top + 200, 400, 300, true);
                                Log.Error("fix invisible window \"{0}\"", GetWindowTitle(hwnd));
                            }
                        }
                    }
                }
            });

            thread.IsBackground = false;
            thread.Start();
        }

        private void WinEventProc(IntPtr hWinEventHook, User32Events eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // only track top level windows
            if (User32.GetParent(hwnd) != IntPtr.Zero)
                return;

            if (eventType == User32Events.EVENT_OBJECT_DESTROY)
            {
                if (idObject != 0)
                {
                    // ignore non-window object (caret etc)
                    return;
                }

                lock (databaseLock)
                {
                    foreach (var key in monitorApplications.Keys)
                    {
                        monitorApplications[key].Remove(hwnd);
                    }

                    bool found = windowTitle.Remove(hwnd);

                    if (sessionActive && found)
                    {
                        StartCaptureTimer(); //update z-order
                    }
                }

                return;
            }

            var window = new SystemWindow(hwnd);

            /* need invisible window event to detect session cut-off
            // only track visible windows
            if (!window.Visible)
            {
                return;
            }
            */

            // auto track taskbar
            if (string.IsNullOrEmpty(window.Title) && !IsTaskBar(window))
            {
                return;
            }

            try
            {
                RECT2 screenPosition = new RECT2();
                User32.GetWindowRect(hwnd, ref screenPosition);
#if DEBUG
                if (window.Title.Contains("Microsoft Visual Studio")
                    && (eventType == User32Events.EVENT_OBJECT_LOCATIONCHANGE
                        || eventType == User32Events.EVENT_SYSTEM_FOREGROUND))
                {
                    return;
                }

                Log.Trace("WinEvent received. Type: {0:x4}, Window: {1:x8}", (uint)eventType, hwnd.ToInt64());

                string log = string.Format("Received message of process {0} at ({1}, {2}) of size {3} x {4} with title: {5}",
                    window.Process.ProcessName,
                    screenPosition.Left,
                    screenPosition.Top,
                    screenPosition.Width,
                    screenPosition.Height,
                    window.Title
                    );
                Log.Trace(log);
#endif

                DateTime now = DateTime.Now;

                if (restoringFromMem)
                {
                    switch (eventType)
                    {
                        case User32Events.EVENT_OBJECT_LOCATIONCHANGE:
                            // let it trigger next restore
                            break;

                        default:
                            // no capture during restore
                            return;
                    }

                    lock (controlLock)
                    {
                        if (restoreTimes >= MinRestoreTimes)
                        {
                            // restore is not finished as long as window location keeps changing
                            StartRestoreTimer();
                        }
                    }
                }
                else if (sessionActive)
                {
                    switch (eventType)
                    {
                        case User32Events.EVENT_OBJECT_LOCATIONCHANGE:
                            lock (controlLock)
                            {
                                if (restoringFromDB)
                                {
                                    /*
                                    if (restoreTimes >= MinRestoreTimes)
                                    {
                                        // restore is not finished as long as window location keeps changing
                                        StartRestoreTimer();
                                    }
                                    */
                                }
                                else
                                {
                                    // If the window move is initiated by OS (before sleep),
                                    // keep restart capture timer would eventually discard these moves
                                    // either by power suspend event handler calling CancelCaptureTimer()
                                    // or due to capture timer handler found too many window moves

                                    // If the window move is caused by user snapping window to screen edge,
                                    // delay capture by a few seconds should be fine.

                                    StartCaptureTimer();

                                    pendingCaptureWindows.Enqueue(hwnd);
                                }
                            }

                            break;

                        case User32Events.EVENT_SYSTEM_FOREGROUND:
                            ActivateWindow(hwnd);
                            goto case User32Events.EVENT_SYSTEM_MINIMIZESTART;
                        case User32Events.EVENT_SYSTEM_MINIMIZESTART:
                        case User32Events.EVENT_SYSTEM_MINIMIZEEND:
                        case User32Events.EVENT_SYSTEM_MOVESIZEEND:
                            // capture user moves
                            // Occasionaly OS might bring a window to forground upon sleep
                            CaptureCursorPos(curDisplayKey);
                            if (restoringFromDB && eventType == User32Events.EVENT_SYSTEM_FOREGROUND)
                            {
                                StartCaptureTimer(milliSeconds: 0); // immediately capture new window
                            }
                            else
                            {
                                StartCaptureTimer();
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }

        private void TrimQueue(string displayKey, IntPtr hwnd)
        {
            while (monitorApplications[displayKey][hwnd].Count > MaxHistoryQueueLength)
            {
                // limit length of capture history
                monitorApplications[displayKey][hwnd].RemoveAt(0);
            }
        }

        private void CaptureCursorPos(string displayKey)
        {
            POINT cursorPos;
            User32.GetCursorPos(out cursorPos);
            lastCursorPos[displayKey] = cursorPos;
        }

        private void RestoreCursorPos(string displayKey)
        {
            POINT cursorPos = lastCursorPos[displayKey];
            User32.SetCursorPos(cursorPos.X, cursorPos.Y);
        }

        private IntPtr GetPrevZorderWindow(IntPtr hWnd)
        {
            if (!User32.IsWindow(hWnd))
            {
                return IntPtr.Zero;
            }

            RECT2 rect = new RECT2();
            User32.GetWindowRect(hWnd, ref rect);
            //if (rect.Width < 10 && rect.Height < 10)
            //    return IntPtr.Zero; //too small to care about

            IntPtr result = hWnd;
            do
            {
                result = User32.GetWindow(result, 3);
                if (result == IntPtr.Zero)
                    break;

                RECT2 prevRect = new RECT2();
                User32.GetWindowRect(result, ref prevRect);

                RECT2 intersection = new RECT2();
                if (User32.IntersectRect(out intersection, ref rect, ref prevRect))
                {
                    if (monitorApplications[curDisplayKey].ContainsKey(result))
                        break;
                }

            } while (true);

            return result;
        }

        public bool IsWindowTopMost(IntPtr hWnd)
        {
            long exStyle = User32.GetWindowLong(hWnd, User32.GWL_EXSTYLE);
            return (exStyle & User32.WS_EX_TOPMOST) != 0;
        }

        // restore z-order might incorrectly put some window to topmost
        // workaround by put these windows behind HWND_NOTOPMOST
        private bool FixTopMostWindowStyle()
        {
            if (!fixZorder)
                return false;

            bool fixedOneWindow = false;
            IntPtr desktopWindow = User32.GetDesktopWindow();
            IntPtr topMostWindow = User32.GetTopWindow(desktopWindow);

            for (IntPtr hwnd = topMostWindow; hwnd != IntPtr.Zero; hwnd = User32.GetWindow(hwnd, 2))
            {
                if (monitorApplications[curDisplayKey].ContainsKey(hwnd))
                {
                    if (!IsWindowTopMost(hwnd))
                        continue;

                    SystemWindow window = new SystemWindow(hwnd);
                    if (IsTaskBar(window))
                        continue;

                    fixedOneWindow = true;
                    bool ok = User32.SetWindowPos(hwnd, new IntPtr(-2), //notopmost
                        0, 0, 0, 0,
                        0
                        | SetWindowPosFlags.DoNotActivate
                        | SetWindowPosFlags.IgnoreMove
                        | SetWindowPosFlags.IgnoreResize
                    );

                    Log.Error("Fix topmost window {0} {1}", GetWindowTitle(hwnd), ok.ToString());
                }
            }

            return fixedOneWindow;
        }

        private int RestoreZorder(IntPtr hWnd, IntPtr prev)
        {
            if (prev == IntPtr.Zero)
            {
                Log.Trace("avoid restore to top most for window {0}", GetWindowTitle(hWnd));
                return 0; // issue 21, avoiding restore to top z-order
            }

            if (!User32.IsWindow(prev))
            {
                return 0;
            }

            SystemWindow window = new SystemWindow(prev);
            if (!window.IsValid())
            {
                return 0;
            }

            if (IsTaskBar(window))
            {
                Log.Trace("avoid restore under taskbar for window {0}", GetWindowTitle(hWnd));
                return 0; // issue 21, avoid restore to top z-order
            }

            bool ok = User32.SetWindowPos(
                hWnd,
                prev,
                0, //rect.Left,
                0, //rect.Top,
                0, //rect.Width,
                0, //rect.Height,
                0
                //| SetWindowPosFlags.DoNotRedraw
                //| SetWindowPosFlags.DoNotSendChangingEvent
                //| SetWindowPosFlags.DoNotChangeOwnerZOrder
                | SetWindowPosFlags.DoNotActivate
                | SetWindowPosFlags.IgnoreMove
                | SetWindowPosFlags.IgnoreResize
            );

            Log.Event("Restore zorder {2} by repositioning window \"{0}\" under \"{1}\"",
                GetWindowTitle(hWnd),
                GetWindowTitle(prev),
                ok ? "succeeded" : "failed");

            return ok ? 1 : -1;
        }

        private bool CaptureWindow(SystemWindow window, User32Events eventType, DateTime now, string displayKey, bool saveToDB = false)
        {
            bool ret = false;
            IntPtr hWnd = window.HWnd;

            if (!monitorApplications.ContainsKey(displayKey))
            {
                monitorApplications.Add(displayKey, new Dictionary<IntPtr, List<ApplicationDisplayMetrics>>());
            }

            ApplicationDisplayMetrics curDisplayMetrics = null;
            if (IsWindowMoved(displayKey, window, eventType, now, out curDisplayMetrics))
            {
                string log = string.Format("Captured {0,-8} at ({1}, {2}) of size {3} x {4} V:{5} {6} ",
                    curDisplayMetrics,
                    curDisplayMetrics.ScreenPosition.Left,
                    curDisplayMetrics.ScreenPosition.Top,
                    curDisplayMetrics.ScreenPosition.Width,
                    curDisplayMetrics.ScreenPosition.Height,
                    window.Visible,
                    curDisplayMetrics.Title
                    );
                string log2 = string.Format("\n    WindowPlacement.NormalPosition at ({0}, {1}) of size {2} x {3}",
                    curDisplayMetrics.WindowPlacement.NormalPosition.Left,
                    curDisplayMetrics.WindowPlacement.NormalPosition.Top,
                    curDisplayMetrics.WindowPlacement.NormalPosition.Width,
                    curDisplayMetrics.WindowPlacement.NormalPosition.Height
                    );
                Log.Trace(log + log2);

                if (!monitorApplications[displayKey].ContainsKey(hWnd))
                {
                    monitorApplications[displayKey].Add(hWnd, new List<ApplicationDisplayMetrics>());
                    monitorApplications[displayKey][hWnd].Add(curDisplayMetrics);
                }
                else
                {
                    TrimQueue(displayKey, hWnd);
                    monitorApplications[displayKey][hWnd].Add(curDisplayMetrics);
                }
                ret = true;
            }

            if (saveToDB && curDisplayMetrics != null && monitorApplications[displayKey].ContainsKey(hWnd))
            {
                try
                {
                    var db = persistDB.GetCollection<ApplicationDisplayMetrics>(displayKey);
                    windowTitle[hWnd] = curDisplayMetrics.Title;
                    curDisplayMetrics.ProcessName = window.Process.ProcessName;

                    IntPtr hProcess = Kernel32.OpenProcess(Kernel32.ProcessAccessFlags.QueryInformation, false, curDisplayMetrics.ProcessId);
                    string procPath = GetProcExePath(hProcess);
                    if (!String.IsNullOrEmpty(procPath))
                    {
                        if (processCmd.ContainsKey(curDisplayMetrics.ProcessId))
                            curDisplayMetrics.ProcessExePath = processCmd[curDisplayMetrics.ProcessId];
                        else
                            curDisplayMetrics.ProcessExePath = procPath;
                    }
                    db.Insert(curDisplayMetrics);
                    Kernel32.CloseHandle(hProcess);
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
            }
            return ret;
        }

        public static string GetDisplayKey()
        {
            DesktopDisplayMetrics metrics = new DesktopDisplayMetrics();
            metrics.AcquireMetrics();
            return metrics.Key;
        }

        private void StartCaptureTimer(int milliSeconds = CaptureLatency)
        {
            // restart capture timer
            captureTimer.Change(milliSeconds, Timeout.Infinite);
        }

        private void CancelCaptureTimer()
        {
            // restart capture timer
            captureTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public void StartRestoreTimer(int milliSecond = RestoreLatency, bool wait = false)
        {
            restoreTimer.Change(milliSecond, Timeout.Infinite);
            if (wait)
            {
                Thread.Sleep(milliSecond);
            }
        }

        private void CancelRestoreTimer()
        {
            restoreTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void StartRestoreFinishedTimer(int milliSecond)
        {
            restoreFinishedTimer.Change(milliSecond, Timeout.Infinite);
        }

        private void CancelRestoreFinishedTimer()
        {
            restoreFinishedTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public void BatchCaptureApplicationsOnCurrentDisplays(bool saveToDB = false)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    lock (databaseLock)
                    {
                        if (restoringFromMem)
                        {
                            return;
                        }

                        string displayKey = GetDisplayKey();
                        if (!displayKey.Equals(curDisplayKey))
                        {
                            Log.Trace("Ignore capture request for non-current display setting {0}", displayKey);
                            return;
                        }
                        CaptureApplicationsOnCurrentDisplays(displayKey, saveToDB);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }

            });
            thread.IsBackground = false;
            thread.Name = "PersistentWindowProcessor.BeginCaptureApplicationsOnCurrentDisplays()";
            thread.Start();
        }

        private void CaptureNewDisplayConfig(string displayKey)
        {
            CaptureApplicationsOnCurrentDisplays(displayKey);
            CaptureApplicationsOnCurrentDisplays(displayKey); // for capture accurate z-order
            RecordLastUserActionTime(DateTime.Now);
            CaptureCursorPos(displayKey);
        }

        private void EndDisplaySession()
        {
            CancelCaptureTimer();
            ResetState();
            //RecordLastUserActionTime(DateTime.Now);
        }

        private void ResetState()
        {
            lock (controlLock)
            {
                // end of restore period
                //CancelRestoreTimer();
                restoreTimes = 0;
                restoreHaltTimes = 0;
                restoreNestLevel = 0;
                restoredWindows.Clear();

                // reset counter of multiwindowProcess
                var keys = new List<string>();
                foreach (var key in multiwindowProcess.Keys)
                {
                    keys.Add(key);
                }

                foreach (var key in keys)
                {
                    multiwindowProcess[key] = 0;
                }
            }
        }

        private void RecordLastUserActionTime(DateTime time, bool force = false)
        {
            lock (controlLock)
            {
                if (!lastUserActionTime.ContainsKey(curDisplayKey))
                {
                    lastUserActionTime.Add(curDisplayKey, time);
                    Log.Trace("Capture time {0}", time);
                }
                else if (force)
                {
                    lastUserActionTime[curDisplayKey] = time;
                    Log.Trace("Capture time {0}", time);
                }
            }
        }

        private void RemoveUserActionTime(bool force = true)
        {
            lock (controlLock)
            {
                if (!force && lastUserActionTime.ContainsKey(curDisplayKey))
                {
                    return;
                }
                lastUserActionTime.Remove(curDisplayKey);
            }

        }

        private void CaptureApplicationsOnCurrentDisplays(string displayKey, bool saveToDB = false)
        {
            Log.Trace("");
            Log.Trace("Capturing windows for display setting {0}", displayKey);
            if (saveToDB)
            {
                var db = persistDB.GetCollection<ApplicationDisplayMetrics>(displayKey);
                db.DeleteAll();
            }

            int pendingEventCnt = pendingCaptureWindows.Count;
            pendingCaptureWindows.Clear();

            if (pendingEventCnt > MinWindowOsMoveEvents)
            {
                // too many pending window moves, they are probably initiated by OS instead of user,
                // defer capture
                StartCaptureTimer();
                Log.Trace("defer capture");
            }
            else
            {
                var appWindows = CaptureWindowsOfInterest();
                DateTime now = DateTime.Now;
                int movedWindows = 0;

                foreach (var window in appWindows)
                {
                    if (CaptureWindow(window, 0, now, displayKey, saveToDB))
                    {
                        movedWindows++;
                    }
                }

                if (pendingEventCnt > 0 && movedWindows > MaxUserMoves)
                {
                    // whether these are user moves is still doubtful
                    // defer acknowledge of user action by one more cycle
                    StartCaptureTimer();
                    Log.Trace("further defer capture");
                }
                else
                {
                    // confirmed user moves
                    RecordLastUserActionTime(time: DateTime.Now, force: true);
                    if (movedWindows > 0)
                        Log.Trace("{0} windows captured", movedWindows);
                }
            }
        }

        private IEnumerable<SystemWindow> CaptureWindowsOfInterest()
        {
            /*
            return SystemWindow.AllToplevelWindows
                                .Where(row =>
                                {
                                    return row.Parent.HWnd.ToInt64() == 0
                                    && row.Visible;
                                });
            */

            List<SystemWindow> result = new List<SystemWindow>();

            IntPtr desktopWindow = User32.GetDesktopWindow();
            IntPtr topMostWindow = User32.GetTopWindow(desktopWindow);

            for (IntPtr hwnd = topMostWindow; hwnd != IntPtr.Zero; hwnd = User32.GetWindow(hwnd, 2))
            {
                if (User32.GetParent(hwnd) != IntPtr.Zero)
                    continue;

                SystemWindow window = new SystemWindow(hwnd);
                if (!window.Visible)
                    continue;

                if (string.IsNullOrEmpty(window.ClassName))
                    continue;

                if (IsTaskBar(window))
                {
                    result.Add(window);
                    continue;
                }

                if (string.IsNullOrEmpty(window.Title))
                    continue;

                // workaround runtime overflow exception in release build
                //WindowStyleFlags style = window.Style;

                long style = User32.GetWindowLong(hwnd, User32.GWL_STYLE);
                if ((style & (long)WindowStyleFlags.MINIMIZEBOX) == 0L)
                    continue;

                /* full screen app such as mstsc may not have maximize box */
                /*
                if ((style & (long)WindowStyleFlags.MAXIMIZEBOX) == 0L)
                {
                        continue;
                }
                */

                result.Add(window);
            }

            return result;
        }

        private bool IsWindowMoved(string displayKey, SystemWindow window, User32Events eventType, DateTime time, out ApplicationDisplayMetrics curDisplayMetrics)
        {
            bool moved = false;
            curDisplayMetrics = null;

            if (!window.IsValid())
            {
                return false;
            }

            IntPtr hwnd = window.HWnd;
            bool isTaskBar = false;
            if (IsTaskBar(window))
            {
                // capture task bar
                isTaskBar = true;
            }

            WindowPlacement windowPlacement = new WindowPlacement();
            User32.GetWindowPlacement(window.HWnd, ref windowPlacement);

            // compensate for GetWindowPlacement() failure to get real coordinate of snapped window
            RECT2 screenPosition = new RECT2();
            User32.GetWindowRect(hwnd, ref screenPosition);

            bool isMinimized = IsMinimized(hwnd);
            uint processId = 0;
            uint threadId = User32.GetWindowThreadProcessId(window.HWnd, out processId);

            bool isFullScreen = IsFullScreen(hwnd);

            curDisplayMetrics = new ApplicationDisplayMetrics
            {
                HWnd = hwnd,
                ProcessId = processId,

                // this function call is very CPU-intensive
                //ProcessName = window.Process.ProcessName,
                ProcessName = "",

                ClassName = window.ClassName,
                Title = isTaskBar ? "$taskbar$" : window.Title,

                //full screen app such as mstsc may not have maximize box
                IsFullScreen = isFullScreen,
                IsMinimized = isMinimized,

                CaptureTime = time,
                WindowPlacement = windowPlacement,
                NeedUpdateWindowPlacement = false,
                ScreenPosition = screenPosition,

                IsTopMost = window.TopMost, //IsWindowTopMost(hwnd),
                NeedClearTopMost = false,

                PrevZorderWindow = GetPrevZorderWindow(hwnd),
                NeedRestoreZorder = false,
            };

            if (!monitorApplications[displayKey].ContainsKey(hwnd))
            {
                //newly created window or new display setting
                if (!windowTitle.ContainsKey(hwnd))
                {
                    windowTitle[hwnd] = curDisplayMetrics.Title;
                }
                moved = true;
            }
            else
            {
                ApplicationDisplayMetrics prevDisplayMetrics;
                if (eventType == 0 && restoringFromMem)
                {
                    //truncate OS move event that happens after cut-off time
                    int truncateSize = 0;
                    foreach (var metrics in monitorApplications[displayKey][hwnd])
                    {
                        if (metrics.CaptureTime > time)
                        {
                            break;
                        }
                        truncateSize++;
                    }

                    if (truncateSize == 0)
                    {
                        Log.Trace("unexpected zero captured events");
                        return false;
                    }
                    else if (truncateSize < monitorApplications[displayKey][hwnd].Count)
                    {
                        // truncate capture history to filter out OS moves
                        monitorApplications[displayKey][hwnd].RemoveRange(truncateSize, monitorApplications[displayKey][hwnd].Count - truncateSize);
                    }
                }
                prevDisplayMetrics = monitorApplications[displayKey][hwnd].Last();

                if (prevDisplayMetrics.ProcessId != curDisplayMetrics.ProcessId
                    || prevDisplayMetrics.ClassName != curDisplayMetrics.ClassName)
                {
                    // key collision between dead window and new window with the same hwnd
                    Log.Error("Invalid entry");
                    monitorApplications[displayKey].Remove(hwnd);
                    moved = true;
                }
                /*
                else if (eventType == User32Events.EVENT_SYSTEM_FOREGROUND)
                {
                    // when close/reopen session, OS/user may activate existing window (possibly with different position)
                    // just ignore it
                }
                */
                else if (!prevDisplayMetrics.EqualPlacement(curDisplayMetrics))
                {
                    //monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement = curDisplayMetrics.WindowPlacement;
                    curDisplayMetrics.NeedUpdateWindowPlacement = true;
                    moved = true;
                }
                else if (!prevDisplayMetrics.ScreenPosition.Equals(curDisplayMetrics.ScreenPosition))
                {
                    moved = true;
                }
                else
                {
                    // nothing changed except event type & time
                }


                if (fixZorder)
                {
                    if (prevDisplayMetrics.IsTopMost != curDisplayMetrics.IsTopMost)
                    {
                        if (!prevDisplayMetrics.IsTopMost && curDisplayMetrics.IsTopMost)
                            curDisplayMetrics.NeedClearTopMost = true;

                        moved = true;
                    }

                    if (prevDisplayMetrics.PrevZorderWindow != curDisplayMetrics.PrevZorderWindow)
                    {
                        if (!moved && curDisplayMetrics.IsMinimized)
                        {
                            ; // ignore z-order change in minimized state
                        }
                        else
                        {
                            curDisplayMetrics.NeedRestoreZorder = true;
                            moved = true;
                        }
                    }
                }

                if (prevDisplayMetrics.IsFullScreen && !prevDisplayMetrics.IsMinimized && curDisplayMetrics.IsMinimized)
                    curDisplayMetrics.IsFullScreen = true; // flag that current state is minimized from full screen mode
            }

            return moved;
        }

        private void BatchRestoreApplicationsOnCurrentDisplays()
        {
            if (restoreTimes == 0)
            {
                if (!iconActive)
                {
                    // fix issue 22, avoid frequent restore tip activation due to fast display setting switch
                    iconActive = true;
                    showRestoreTip();
                }

                if (restoringFromDB)
                {
                    RecordLastUserActionTime(DateTime.Now);
                }
            }

            lock (controlLock)
            {
                if (!restoringFromMem && !restoringFromDB)
                {
                    return;
                }

                if (restoreNestLevel > 0)
                {
                    // avoid overloading CPU due to too many restore threads ready to run
                    Log.Trace("restore busy");
                    StartRestoreTimer();
                    return;
                }
                restoreNestLevel++;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    lock (databaseLock)
                    {
                        CancelRestoreFinishedTimer();
                        string displayKey = GetDisplayKey();
                        if (!displayKey.Equals(curDisplayKey))
                        {
                            // display resolution changes during restore
                            ++restoreHaltTimes;
                            if (restoreHaltTimes > 5)
                            {
                                restoreHaltTimes = 0;
                                // immediately finish restore
                                StartRestoreFinishedTimer(0);
                            }
                            else
                            {
                                restoreTimes = 0;
                                StartRestoreTimer();
                            }
                        }
                        else if (restoreTimes < (remoteSession ? MaxRestoreTimesRemote : MaxRestoreTimesLocal))
                        {
                            try
                            {
                                RestoreApplicationsOnCurrentDisplays(displayKey);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex.ToString());
                            }

                            restoreTimes++;

                            // schedule finish restore
                            StartRestoreFinishedTimer(milliSecond: MaxRestoreLatency);

                            // force next restore, as Windows OS might not send expected message during restore
                            if (restoreTimes < MinRestoreTimes)
                            {
                                StartRestoreTimer();
                            }
                        }
                        else
                        {
                            // immediately finish restore
                            StartRestoreFinishedTimer(0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }

                lock (controlLock)
                {
                    restoreNestLevel--;
                }
            });
            thread.IsBackground = false;
            thread.Name = "PersistentWindowProcessor.RestoreApplicationsOnCurrentDisplays()";
            thread.Start();
        }

        private bool IsTaskBar(SystemWindow window)
        {
            if (!window.IsValid() || !window.Visible || string.IsNullOrEmpty(window.ClassName))
            {
                return false;
            }

            return window.ClassName.Equals("Shell_TrayWnd");
        }

        private void TestMoveTaskBar()
        {
            Thread.Sleep(3000);
            IntPtr hwnd = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            MoveTaskBar(hwnd, 300, 15);
        }

        private void RestoreFullScreenWindow(IntPtr hwnd)
        {
            long style = User32.GetWindowLong(hwnd, User32.GWL_STYLE);
            if ((style & (long)WindowStyleFlags.CAPTION) == 0L)
            {
                return;
            }

            RECT2 screenPosition = new RECT2();
            User32.GetWindowRect(hwnd, ref screenPosition);

            // window caption center might be occupied by other controls 
            int centerx = screenPosition.Left + screenPosition.Width / 4;

            int centery = screenPosition.Top + 15;
            User32.SetCursorPos(centerx, centery);
            User32.SetActiveWindow(hwnd);
            User32.mouse_event(MouseAction.MOUSEEVENTF_LEFTDOWN | MouseAction.MOUSEEVENTF_LEFTUP,
                0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(150);
            User32.mouse_event(MouseAction.MOUSEEVENTF_LEFTDOWN | MouseAction.MOUSEEVENTF_LEFTUP,
                0, 0, 0, UIntPtr.Zero);

            Log.Error("restore full screen window {0}", GetWindowTitle(hwnd));
        }

        private void MoveTaskBar(IntPtr hwnd, int x, int y)
        {
            // simulate mouse drag, assuming taskbar is unlocked
            /*
                ControlGetPos x, y, w, h, MSTaskListWClass1, ahk_class Shell_TrayWnd
                MouseMove x+1, y+1
                MouseClickDrag Left, x+1, y+1, targetX, targetY, 10
            */

            RECT2 screenPosition = new RECT2();
            IntPtr hReBar = User32.FindWindowEx(hwnd, IntPtr.Zero, "ReBarWindow32", null);
            //User32.GetWindowRect(hReBar, ref screenPosition);

            IntPtr hTaskBar = User32.FindWindowEx(hReBar, IntPtr.Zero, "MSTaskSwWClass", null);
            hTaskBar = User32.FindWindowEx(hTaskBar, IntPtr.Zero, "MSTaskListWClass", null);
            User32.GetWindowRect(hTaskBar, ref screenPosition);

            // try place cursor to head and then tail of taskbar to guarantee move success
            int dx;
            int dy;
            if (screenPosition.Width > screenPosition.Height)
            {
                switch (restoreTimes)
                {
                    case 1:
                        dx = screenPosition.Width - restoreTimes * 10;
                        break;
                    default:
                        dx = 1;
                        break;
                }
                dy = screenPosition.Height / 2;
            }
            else
            {
                dx = screenPosition.Width / 2;
                switch (restoreTimes)
                {
                    case 1:
                        dy = screenPosition.Height - restoreTimes * 10;
                        break;
                    default:
                        dy = 1;
                        break;
                }
            }

            // avoid unnecessary move
            int centerx = screenPosition.Left + screenPosition.Width / 2;
            int centery = screenPosition.Top + screenPosition.Height / 2;
            int deltax = Math.Abs(centerx - x);
            int deltay = Math.Abs(centery - y);
            if (deltax + deltay < 300)
            {
                // taskbar center has no change
                return;
            }

            User32.SetCursorPos(screenPosition.Left + dx, screenPosition.Top + dy);
            User32.SetActiveWindow(hTaskBar);
            User32.mouse_event(MouseAction.MOUSEEVENTF_LEFTDOWN,
                0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(3500); // wait to be activated
            User32.SetCursorPos(x, y);
            User32.mouse_event(MouseAction.MOUSEEVENTF_LEFTUP,
                0, 0, 0, UIntPtr.Zero);
        }

        private ApplicationDisplayMetrics SearchDb(IEnumerable<ApplicationDisplayMetrics> results)
        {
            foreach (var result in results)
            {
                if (dbMatchWindow.ContainsKey(result.Id))
                {
                    continue;
                }

                // map to the first matching db entry
                Log.Trace("restore window position with matching process name {0}", result.ProcessName);
                return result;
            }

            return null;
        }

        private bool RestoreApplicationsOnCurrentDisplays(string displayKey, SystemWindow sWindow = null)
        {
            bool succeed = false;

            if (!monitorApplications.ContainsKey(displayKey)
                || monitorApplications[displayKey].Count == 0)
            {
                // the display setting has not been captured yet
                Log.Trace("Restoring new display setting {0}", displayKey);
                CaptureNewDisplayConfig(displayKey);
                return succeed;
            }

            Log.Info("");
            Log.Info("Restoring windows pass {0} for {1}", restoreTimes, displayKey);

            /*
            if (restoreTimes == 0)
                RestoreCursorPos(displayKey);
            */

            IEnumerable<SystemWindow> sWindows;
            SystemWindow[] arr = new SystemWindow[1];
            if (sWindow != null)
            {
                arr[0] = sWindow;
                sWindows = arr;
            }
            else
            {
                sWindows = CaptureWindowsOfInterest();
            }

            // determine the time to be restored
            DateTime lastCaptureTime;
            if (lastUserActionTime.ContainsKey(displayKey))
            {
                lastCaptureTime = lastUserActionTime[displayKey];

                if (restoringFromMem)
                {
                    // further dial last capture time back in case it is too close to now (actual restore time)
                    DateTime now = DateTime.Now;
                    TimeSpan ts = new TimeSpan(0, 0, 0, 0, MinCaptureToRestoreLatency);
                    if (lastCaptureTime + ts > now)
                    {
                        Log.Error("Last capture time {0} is too close to restore time {1}", lastCaptureTime, now);
                        lastCaptureTime = now.Subtract(ts);
                        lastUserActionTime[displayKey] = lastCaptureTime;
                    }
                }
            }
            else
            {
                Log.Error("Missing session cut-off time for display setting {0}", displayKey);
                lastCaptureTime = DateTime.Now;
            }

            DateTime printRestoreTime = lastCaptureTime;
            ILiteCollection<ApplicationDisplayMetrics> db = null;
            if (restoringFromDB)
            {
                db = persistDB.GetCollection<ApplicationDisplayMetrics>(displayKey);

                foreach (var window in sWindows)
                {
                    if (!window.IsValid() || string.IsNullOrEmpty(window.ClassName))
                    {
                        continue;
                    }

                    IntPtr hWnd = window.HWnd;
                    if (!monitorApplications[displayKey].ContainsKey(hWnd))
                    {
                        continue;
                    }

                    ApplicationDisplayMetrics curDisplayMetrics = null;
                    var processName = window.Process.ProcessName;
                    uint processId = 0;
                    uint threadId = User32.GetWindowThreadProcessId(hWnd, out processId);

                    if (windowTitle.ContainsKey(hWnd))
                    {
                        string title = windowTitle[hWnd];
                        var results = db.Find(x => x.ClassName == window.ClassName && x.Title == title && x.ProcessName == processName && x.ProcessId == processId);
                        curDisplayMetrics = SearchDb(results);

                        if (curDisplayMetrics == null)
                        {
                            results = db.Find(x => x.ClassName == window.ClassName && x.Title == title && x.ProcessName == processName);
                            curDisplayMetrics = SearchDb(results);
                        }
                    }

                    if (curDisplayMetrics == null)
                    {
                        var results = db.Find(x => x.ClassName == window.ClassName && x.ProcessName == processName);
                        curDisplayMetrics = SearchDb(results);
                    }

                    if (curDisplayMetrics == null)
                    {
                        // no db data to restore
                        continue;
                    }

                    // update stale window/process id
                    curDisplayMetrics.HWnd = hWnd;
                    curDisplayMetrics.ProcessId = processId;
                    curDisplayMetrics.ProcessName = processName;

                    if (dbMatchWindow.ContainsKey(curDisplayMetrics.Id))
                    {
                        continue; //avoid restore multiple times
                    }

                    dbMatchWindow.Add(curDisplayMetrics.Id, true);

                    printRestoreTime = curDisplayMetrics.CaptureTime;
                    curDisplayMetrics.CaptureTime = lastCaptureTime;

                    TrimQueue(displayKey, hWnd);
                    monitorApplications[displayKey][hWnd].Add(curDisplayMetrics);
                }
            }

            Log.Trace("Restore time {0}", printRestoreTime);
            if (restoreTimes == 0)
            {
                Log.Event("Start restoring window layout back to {0} for display setting {1}", printRestoreTime, curDisplayKey);
            }

            foreach (var window in sWindows)
            {
                if (!window.IsValid())
                {
                    continue;
                }

                IntPtr hWnd = window.HWnd;
                if (!monitorApplications[displayKey].ContainsKey(hWnd))
                {
                    continue;
                }

                ApplicationDisplayMetrics curDisplayMetrics = null;
                if (!IsWindowMoved(displayKey, window, 0, lastCaptureTime, out curDisplayMetrics))
                    continue;

                ApplicationDisplayMetrics prevDisplayMetrics = monitorApplications[displayKey][hWnd].Last();
                RECT2 rect = prevDisplayMetrics.ScreenPosition;
                if (rect.Top < 0 && rect.Top > -15)
                {
                    Log.Error("Auto correct negative y screen coordinate for window {0}", GetWindowTitle(hWnd));
                    // automatically fix small negative y coordinate to avoid repeated recovery failure
                    rect.Top = 0;
                }
                WindowPlacement windowPlacement = prevDisplayMetrics.WindowPlacement;

                if (IsTaskBar(window))
                {
                    if (!dryRun)
                    {
                        MoveTaskBar(hWnd, rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                        restoredWindows.Add(hWnd);
                        //RestoreCursorPos(displayKey);
                    }
                    continue;
                }

                if (!dryRun)
                {
                    if (prevDisplayMetrics.IsMinimized)
                    {
                        User32.ShowWindow(hWnd, User32.SW_SHOWMINNOACTIVE);
                        Log.Error("recover minimized window {0}", GetWindowTitle(hWnd));
                        continue;
                    }
                }

                if (fixZorder && restoringFromMem && curDisplayMetrics.NeedClearTopMost)
                {
                    bool ok = User32.SetWindowPos(hWnd, new IntPtr(-2), //notopmost
                        0, 0, 0, 0,
                        0
                        | SetWindowPosFlags.DoNotActivate
                        | SetWindowPosFlags.IgnoreMove
                        | SetWindowPosFlags.IgnoreResize
                    );

                    Log.Error("Fix topmost window {0} {1}", GetWindowTitle(hWnd), ok.ToString());
                }

                if (fixZorder && restoringFromMem && curDisplayMetrics.NeedRestoreZorder && restoreTimes > 0)
                {
                    RestoreZorder(hWnd, prevDisplayMetrics.PrevZorderWindow);
                }

                bool success = true;
                if (restoreTimes >= MinRestoreTimes || curDisplayMetrics.NeedUpdateWindowPlacement)
                {
                    // recover NormalPosition (the workspace position prior to snap)
                    if (windowPlacement.ShowCmd == ShowWindowCommands.Maximize && !dryRun)
                    {
                        // When restoring maximized windows, it occasionally switches res and when the maximized setting is restored
                        // the window thinks it's maximized, but does not eat all the real estate. So we'll temporarily unmaximize then
                        // re-apply that
                        windowPlacement.ShowCmd = ShowWindowCommands.Normal;
                        User32.SetWindowPlacement(hWnd, ref windowPlacement);
                        windowPlacement.ShowCmd = ShowWindowCommands.Maximize;
                    }
                    else if (restoreTimes == 0 && prevDisplayMetrics.IsFullScreen && !prevDisplayMetrics.IsMinimized && windowPlacement.ShowCmd == ShowWindowCommands.Normal && !dryRun)
                    {
                        Log.Error("recover full screen window {0}", GetWindowTitle(hWnd));
                        windowPlacement.ShowCmd = ShowWindowCommands.Minimize;
                        User32.SetWindowPlacement(hWnd, ref windowPlacement);
                        windowPlacement.ShowCmd = ShowWindowCommands.Normal;
                    }

                    if (!dryRun)
                    {
                        success &= User32.SetWindowPlacement(hWnd, ref windowPlacement);
                    }
                    Log.Info("SetWindowPlacement({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                        window.Process.ProcessName,
                        windowPlacement.NormalPosition.Left,
                        windowPlacement.NormalPosition.Top,
                        windowPlacement.NormalPosition.Width,
                        windowPlacement.NormalPosition.Height,
                        success);
                }

                // recover previous screen position
                if (!dryRun)
                {
                    success &= User32.MoveWindow(hWnd, rect.Left, rect.Top, rect.Width, rect.Height, true);
                    if (prevDisplayMetrics.IsFullScreen && windowPlacement.ShowCmd == ShowWindowCommands.Normal && !prevDisplayMetrics.IsMinimized)
                    {
                        RestoreFullScreenWindow(hWnd);
                    }
                    restoredWindows.Add(hWnd);

                    Log.Info("MoveWindow({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                        window.Process.ProcessName,
                        rect.Left,
                        rect.Top,
                        rect.Width,
                        rect.Height,
                        success);
                }

                succeed = true;
                if (!success)
                {
                    string error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Log.Error(error);
                }

            }

            Log.Trace("Restored windows position for display setting {0}", displayKey);

            if (restoringFromDB && restoreTimes == 0)
            {
                // launch process in db
                var results = db.FindAll(); // find process not yet started
                foreach (var curDisplayMetrics in results)
                {
                    if (dbMatchWindow.ContainsKey(curDisplayMetrics.Id))
                    {
                        continue;
                    }
#if DEBUG
                    if (curDisplayMetrics.Title.Contains("Microsoft Visual Studio"))
                    {
                        continue;
                    }
#endif

                    if (multiwindowProcess.ContainsKey(curDisplayMetrics.ProcessName))
                    {
                        if (multiwindowProcess[curDisplayMetrics.ProcessName] > 0)
                        {
                            // already launched
                            continue;
                        }
                        multiwindowProcess[curDisplayMetrics.ProcessName]++;
                    }

                    if (!String.IsNullOrEmpty(curDisplayMetrics.ProcessExePath))
                    {
                        if (!dryRun)
                        {
                            try
                            {
                                string processPath = curDisplayMetrics.ProcessExePath;
                                foreach (var processName in realProcessFileName.Keys)
                                {
                                    if (processPath.Contains(processName))
                                    {
                                        processPath = processPath.Replace(processName, realProcessFileName[processName]);
                                        break;
                                    }
                                }

                                Log.Event("launch process {0}", processPath);
                                string batFile = Path.Combine(appDataFolder, "pw_exec.bat");
                                File.WriteAllText(batFile, "start \"\" " + processPath);
                                //Process.Start(batFile);
                                //Process process = Process.Start("cmd.exe", "-c " + batFile);
                                Process.Start("explorer.exe", batFile);
                                Thread.Sleep(2000);
                                File.Delete(batFile);
                                /*
                                if (!process.HasExited)
                                    process.Kill();
                                */
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex.ToString());
                            }
                        }
                    }
                }
            }

            return succeed;
        }

        private string GetProcExePath(IntPtr hProc)
        {
            string pathToExe = string.Empty;

            int nChars = 4096;
            StringBuilder buf = new StringBuilder(nChars);

            bool success = Kernel32.QueryFullProcessImageName(hProc, 0, buf, ref nChars);

            if (success)
            {
                pathToExe = buf.ToString();
            }
            /*
            else
            {
                // fail to get taskmgr process path, need admin privilege
                int error = Marshal.GetLastWin32Error();
                pathToExe = ("Error = " + error + " when calling GetProcessImageFileName");
            }
            */

            return pathToExe;
        }

        private List<IntPtr> GetWindows(string procName)
        {
            List<IntPtr> result = new List<IntPtr>();
            foreach (var hwnd in monitorApplications[curDisplayKey].Keys)
            {
                SystemWindow window = new SystemWindow(hwnd);
                string pName = window.Process.ProcessName;
                if (pName.Equals(procName))
                {
                    result.Add(hwnd);
                }
            }

            return result;
        }

        private void TestSetWindowPos()
        {
            IntPtr[] w = GetWindows("notepad").ToArray();
            if (w.Length < 2)
                return;

            bool ok = User32.SetWindowPos(
                w[0],
                w[1],
                0, //rect.Left,
                0, //rect.Top,
                0, //rect.Width,
                0, //rect.Height,
                0
                //| SetWindowPosFlags.DoNotRedraw
                //| SetWindowPosFlags.DoNotSendChangingEvent
                | SetWindowPosFlags.DoNotChangeOwnerZOrder
                | SetWindowPosFlags.DoNotActivate
                | SetWindowPosFlags.IgnoreMove
                | SetWindowPosFlags.IgnoreResize
            );
        }

        public void StopRunningThreads()
        {
            //stop running thread of event loop
        }

        #region IDisposable
        public virtual void Dispose(bool disposing)
        {
            StopRunningThreads();

            if (initialized)
            {
                SystemEvents.DisplaySettingsChanging -= this.displaySettingsChangingHandler;
                SystemEvents.DisplaySettingsChanged -= this.displaySettingsChangedHandler;
                SystemEvents.PowerModeChanged -= powerModeChangedHandler;
                SystemEvents.SessionSwitch -= sessionSwitchEventHandler;

                foreach (var handle in this.winEventHooks)
                {
                    User32.UnhookWinEvent(handle);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PersistentWindowProcessor()
        {
            Dispose(false);
        }
        #endregion
    }

}
