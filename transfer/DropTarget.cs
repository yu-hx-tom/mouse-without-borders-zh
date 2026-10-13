using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace MwbTransfer
{
    // Read-only hit testing. No selection, navigation, clipboard, or file writes.
    public static class DropTarget
    {
        private static IntPtr ignoredWindow;
        private static IntPtr ignoredPreviewWindow;
        private static IntPtr ignoredDragIconWindow;
        private static int busy;
        [ThreadStatic] private static string lastError;
        public static string LastError { get { return lastError; } private set { lastError=value; } }

        public sealed class Snapshot
        {
            internal IntPtr Window;
            internal POINT Point;
            internal uint ProcessId;
            internal RECT WindowBounds;
            internal bool Desktop;
        }

        public sealed class ResolvedTarget
        {
            public string Directory, DisplayName;
            public System.Drawing.Rectangle Bounds;
            public bool Background;
        }

        internal sealed class ShellItem
        {
            internal string Name, Path;
            internal bool IsFolder, IsLink, IsFileSystem;
        }

        public static void IgnoreWindow(IntPtr overlay)
        {
            ignoredWindow = overlay == IntPtr.Zero ? IntPtr.Zero : GetAncestor(overlay, 2);
        }

        public static void IgnorePreviewWindow(IntPtr preview)
        {
            ignoredPreviewWindow = preview == IntPtr.Zero ? IntPtr.Zero : GetAncestor(preview, 2);
        }

        internal static void IgnoreDragIconWindow(IntPtr icon)
        {
            ignoredDragIconWindow = icon == IntPtr.Zero ? IntPtr.Zero : GetAncestor(icon, 2);
        }

        public static Snapshot CaptureSnapshot(IntPtr overlay)
        {
            IntPtr context = EnterCoordinateContext();
            try { return CaptureSnapshotCore(overlay); }
            finally { RestoreCoordinateContext(context); }
        }

        private static Snapshot CaptureSnapshotCore(IntPtr overlay)
        {
            POINT point;
            if (!GetPhysicalCursorPos(out point)) { Fail("无法读取松开鼠标时的位置。"); return null; }
            IntPtr ignored = overlay == IntPtr.Zero ? IntPtr.Zero : GetAncestor(overlay, 2);
            IntPtr window = FindTopWindow(point, ignored);
            if (window == IntPtr.Zero) { Fail("鼠标下没有可识别的文件夹窗口，请选择接收文件夹。"); return null; }
            string className = WindowClass(window);
            bool desktop = IsDesktopHost(className);
            if (!desktop && className != "CabinetWClass" && className != "ExploreWClass")
            { Fail("请拖到资源管理器内容区或桌面上的实际文件夹图标，或手动选择接收文件夹。"); return null; }
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            RECT bounds;
            if (!GetWindowRect(window, out bounds)) { Fail("目标资源管理器窗口已经关闭。"); return null; }
            LastError = null;
            return new Snapshot { Window = window, Point = point, ProcessId = pid, WindowBounds = bounds, Desktop = desktop };
        }

        // Call from the receive worker's STA thread while impersonating the interactive user.
        public static string Resolve(Snapshot snapshot)
        {
            ResolvedTarget result = ResolveTarget(snapshot);
            return result == null ? null : result.Directory;
        }

        public static ResolvedTarget ResolveTarget(Snapshot snapshot)
        {
            IntPtr context = EnterCoordinateContext();
            try { return ResolveSnapshot(snapshot); }
            finally { RestoreCoordinateContext(context); }
        }

        private static ResolvedTarget ResolveSnapshot(Snapshot snapshot)
        {
            if (snapshot == null) return null;
            uint pid = snapshot.ProcessId;
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                using (Process current = Process.GetCurrentProcess())
                {
                    if (process.SessionId != current.SessionId || !String.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                        return FailTarget("目标不是当前会话中的资源管理器。");
                }
            }
            catch { return FailTarget("目标资源管理器窗口已经关闭。"); }
            try
            {
                RECT bounds;
                if (!GetWindowRect(snapshot.Window, out bounds) || !bounds.Equals(snapshot.WindowBounds))
                    return FailTarget("松开鼠标后目标窗口已移动，请重新选择接收目录。");
                string error;
                ResolvedTarget result = ResolveCore(snapshot.Window, snapshot.Point, pid, snapshot.Desktop, out error);
                if (!GetWindowRect(snapshot.Window, out bounds) || !bounds.Equals(snapshot.WindowBounds))
                    return FailTarget("接收目录窗口已移动，请重新选择目录。");
                LastError = error;
                return result;
            }
            catch (Exception ex) { return FailTarget("无法读取目标文件夹（" + ex.GetType().Name + "），请手动选择。"); }
        }

        public static string Capture()
        {
            Snapshot snapshot = CaptureSnapshot(ignoredWindow);
            if (snapshot == null) return null;
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                return Fail("资源管理器仍在响应上一次目录查询，请手动选择接收文件夹。");

            string answer = null;
            string error = null;
            Thread thread = new Thread(delegate()
            {
                try { answer = Resolve(snapshot); error = LastError; }
                catch (Exception ex) { error = "无法读取目标文件夹（" + ex.GetType().Name + "），请手动选择。"; }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
            thread.IsBackground = true;
            thread.Name = "MWB drop target";
            thread.SetApartmentState(ApartmentState.STA);
            try { thread.Start(); }
            catch { Interlocked.Exchange(ref busy, 0); return Fail("无法启动目录查询。"); }
            // A stalled Explorer must not stall the mouse/keyboard hook indefinitely.
            if (!thread.Join(1800)) return Fail("资源管理器的目录查询超时，请手动选择接收文件夹。");
            LastError = error;
            return answer;
        }

        private static ResolvedTarget ResolveCore(IntPtr window, POINT point, uint pid, bool desktop, out string error)
        {
            error = null;
            IntPtr view = IntPtr.Zero;
            EnumChildWindows(window, delegate(IntPtr child, IntPtr unused)
            {
                RECT bounds;
                if (WindowClass(child) == "SHELLDLL_DefView" && IsWindowVisible(child) && GetWindowRect(child, out bounds) && bounds.Contains(point))
                { view = child; return false; }
                return true;
            }, IntPtr.Zero);
            if (view == IntPtr.Zero) { error = "松开的位置不在文件夹内容区，请手动选择接收文件夹。"; return null; }

            string itemName;
            bool background;
            System.Drawing.Rectangle itemBounds;
            if (!HitFileListCore(view, point, out itemName, out background, out itemBounds, out error)) return null;
            if (desktop && background)
            { error = "松开的位置是桌面空白处，请拖到实际文件夹图标或手动选择目录。"; return null; }

            var objects = new List<object>();
            try
            {
                object shell = Track(objects, Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)));
                if (desktop)
                {
                    // Namespace 0 is the merged desktop view (per-user, public, and virtual items).
                    // The Shell supplies each item's real path. Never derive it from a visible name.
                    object folder = Track(objects, Call(shell, "NameSpace", 0));
                    if (folder == null) { error = "无法读取当前用户的桌面目录，请手动选择接收文件夹。"; return null; }
                    string result = ResolveItem(ReadFolderItems(folder), itemName, true, out error);
                    if (result == null) return null;
                    string currentName;
                    bool currentBackground;
                    System.Drawing.Rectangle currentBounds;
                    if (!HitFileListCore(view, point, out currentName, out currentBackground, out currentBounds, out error)) return null;
                    if (currentBackground || !String.Equals(itemName, currentName, StringComparison.Ordinal))
                    { error = "桌面图标在解析时发生变化，请重新选择接收目录。"; return null; }
                    uint stillPid;
                    GetWindowThreadProcessId(window, out stillPid);
                    if (stillPid != pid || !IsWindowVisible(view))
                    { error = "桌面窗口已经变化，请重新选择接收目录。"; return null; }
                    return new ResolvedTarget { Directory = result, DisplayName = currentName, Bounds = currentBounds };
                }
                object windows = Track(objects, Call(shell, "Windows"));
                int count = Convert.ToInt32(Get(windows, "Count"));
                // v9.0.0.7: Windows 11 Explorer tabs share one top-level HWND, so the
                // HWND alone can select a background tab. Match the tab that owns the
                // hit SHELLDLL_DefView through IShellBrowser::GetWindow instead.
                IntPtr tab = AncestorOfClass(view, "ShellTabWindowClass", window);
                object chosen = null, single = null;
                int candidates = 0;
                IntPtr singleTab = IntPtr.Zero;
                for (int i = 0; i < count && chosen == null; i++)
                {
                    object candidate = Track(objects, Call(windows, "Item", i));
                    if (candidate == null || Convert.ToInt64(Get(candidate, "HWND")) != window.ToInt64()) continue;
                    candidates++;
                    IntPtr owner = BrowserTabWindow(candidate);
                    if (tab != IntPtr.Zero && owner == tab) { chosen = candidate; break; }
                    if (candidates == 1) { single = candidate; singleTab = owner; }
                }
                // Without tabs there is exactly one browser; accept it only when its
                // window cannot contradict the hit tab (unknown, frame or that tab).
                // v9.0.0.8 (stricter): when the hit view sits in a ShellTabWindowClass, only
                // a browser whose IShellBrowser window IS that tab is accepted (loop above).
                // Without any tab window (older layouts) a single browser is accepted only
                // if it does not report another window. Everything else uses the picker.
                if (chosen == null && candidates == 1 && tab == IntPtr.Zero && (singleTab == IntPtr.Zero || singleTab == window)) chosen = single;
                if (chosen == null)
                {
                    error = candidates > 1 ? "该资源管理器窗口有多个标签，无法确认松开位置所在的标签，请手动选择接收文件夹。" : "无法连接当前用户的资源管理器，请手动选择接收文件夹。";
                    return null;
                }
                {
                    object browser = chosen;
                    object document = Track(objects, Get(browser, "Document"));
                    object folder = Track(objects, Get(document, "Folder"));
                    object self = Track(objects, Get(folder, "Self"));
                    string current = NormalizeDirectory(Convert.ToString(Get(self, "Path")));
                    if (current == null) { error = "此位置是虚拟目录或当前用户无法访问的目录，请选择实际文件夹。"; return null; }
                    string result = current;
                    if (!background)
                    {
                        result = ResolveItem(ReadFolderItems(folder), itemName, false, out error);
                        if (result == null) return null;
                    }
                    RECT stillHere;
                    uint stillPid;
                    GetWindowThreadProcessId(window, out stillPid);
                    if (stillPid != pid || !GetWindowRect(view, out stillHere) || !stillHere.Contains(point))
                    { error = "接收目录窗口已移动或关闭，请重新选择目录。"; return null; }
                    // Recheck navigation after resolving items. No writes occurred during detection.
                    object nowFolder = Track(objects, Get(document, "Folder"));
                    object nowSelf = Track(objects, Get(nowFolder, "Self"));
                    string nowPath = NormalizeDirectory(Convert.ToString(Get(nowSelf, "Path")));
                    if (!String.Equals(current, nowPath, StringComparison.OrdinalIgnoreCase))
                    { error = "接收窗口已切换目录，请重新选择目录。"; return null; }
                    // Confirm the same item or blank area still occupies the release point.
                    string currentName;
                    bool currentBackground;
                    System.Drawing.Rectangle currentBounds;
                    if (!HitFileListCore(view, point, out currentName, out currentBackground, out currentBounds, out error)) return null;
                    if (background != currentBackground || !String.Equals(itemName, currentName, StringComparison.Ordinal))
                    { error = "文件夹项目在解析时发生变化，请重新选择接收目录。"; return null; }
                    return new ResolvedTarget { Directory = result, DisplayName = background ? Convert.ToString(Get(nowSelf, "Name")) : currentName, Bounds = currentBounds, Background = background };
                }
            }
            finally
            {
                for (int i = objects.Count - 1; i >= 0; i--) Release(objects[i]);
            }
        }

        private static List<ShellItem> ReadFolderItems(object folder)
        {
            object items = Call(folder, "Items");
            ConfigureProxy(items);
            try
            {
                int count = Convert.ToInt32(Get(items, "Count"));
                if (count > 4096) throw new IOException("该目录项目过多，请手动选择接收文件夹。");
                var result = new List<ShellItem>();
                for (int i = 0; i < count; i++)
                {
                    object item = Call(items, "Item", i);
                    try
                    {
                        ConfigureProxy(item);
                        if (item != null) result.Add(new ShellItem {
                            Name = Convert.ToString(Get(item, "Name")), Path = Convert.ToString(Get(item, "Path")),
                            IsFolder = Convert.ToBoolean(Get(item, "IsFolder")), IsLink = Convert.ToBoolean(Get(item, "IsLink")),
                            IsFileSystem = Convert.ToBoolean(Get(item, "IsFileSystem"))
                        });
                    }
                    finally { Release(item); }
                }
                return result;
            }
            finally { Release(items); }
        }

        internal static string ResolveItem(IEnumerable<ShellItem> items, string itemName, bool desktop, out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(itemName)) { error = "没有命中实际文件夹图标，请手动选择目录。"; return null; }
            int matches = 0;
            ShellItem match = null;
            foreach (ShellItem item in items)
            {
                if (item != null && String.Equals(item.Name, itemName, StringComparison.OrdinalIgnoreCase))
                { matches++; match = item; }
            }
            if (matches != 1)
            { error = matches > 1 ? "桌面或目录中存在同名项目，无法确定接收位置，请手动选择。" : "无法将图标对应到实际目录，请手动选择。"; return null; }
            // Recycle Bin, This PC, folder shortcuts, and non-file-system namespace extensions are not directories.
            if (!match.IsFolder || match.IsLink || (desktop && !match.IsFileSystem))
            { error = "该图标是文件、快捷方式或虚拟项目，请选择实际文件夹。"; return null; }
            if (desktop && (String.IsNullOrEmpty(match.Path) || match.Path.StartsWith("\\\\", StringComparison.Ordinal)))
            { error = "该桌面图标不对应本机实际文件夹，请手动选择目录。"; return null; }
            if (desktop)
            {
                try
                {
                    string root = Path.GetPathRoot(match.Path);
                    if (String.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network)
                    { error = "网络映射目录不支持自动桌面落点，请手动选择本机文件夹。"; return null; }
                }
                catch { error = "该桌面图标没有有效的本机目录路径，请手动选择目录。"; return null; }
            }
            string directory = NormalizeDirectory(match.Path);
            if (directory == null) error = "该文件夹无法访问或是重定向目录，请手动选择接收文件夹。";
            return directory;
        }

        internal static bool IsDesktopHost(string className) { return className == "Progman" || className == "WorkerW"; }

        // UI Automation rectangles are physical pixels; keep native hit testing in the same coordinate space.
        private static IntPtr EnterCoordinateContext()
        {
            try { IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); return previous != IntPtr.Zero ? previous : SetThreadDpiAwarenessContext(new IntPtr(-3)); }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
        }
        private static void RestoreCoordinateContext(IntPtr previous)
        { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }

        private static bool HitFileList(IntPtr view, POINT point, out string itemName, out bool background, out string error)
        {
            System.Drawing.Rectangle bounds;
            return HitFileListCore(view, point, out itemName, out background, out bounds, out error);
        }

        private static bool HitFileListCore(IntPtr view, POINT point, out string itemName, out bool background,
            out System.Drawing.Rectangle itemBounds, out string error)
        {
            itemName = null;
            background = false;
            itemBounds = System.Drawing.Rectangle.Empty;
            error = null;
            // The desktop uses SysListView32. Its UIA list may be the view root, missing from
            // Descendants, or absent behind a drag overlay. Ask this exact native control for
            // its hit, rather than depending on a particular Windows 10/11 automation tree.
            IntPtr nativeList = WindowClass(view) == "SysListView32" ? view : IntPtr.Zero;
            bool ambiguous = false;
            EnumChildWindows(view, delegate(IntPtr child, IntPtr unused)
            {
                RECT bounds;
                if (WindowClass(child) == "SysListView32" && IsWindowVisible(child) && GetWindowRect(child, out bounds) && bounds.Contains(point))
                {
                    if (nativeList != IntPtr.Zero) { ambiguous = true; return false; }
                    nativeList = child;
                }
                return true;
            }, IntPtr.Zero);
            if (ambiguous) { error = "目标文件列表不明确，请手动选择接收文件夹。"; return false; }
            if (nativeList != IntPtr.Zero)
                return HitNativeList(nativeList, point, out itemName, out background, out itemBounds, out error);

            AutomationElement root = AutomationElement.FromHandle(view);
            Condition listCondition = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
            AutomationElementCollection lists = root.FindAll(TreeScope.Subtree, listCondition);
            AutomationElement list = null;
            for (int i = 0; i < lists.Count; i++)
            {
                AutomationElement candidate = lists[i];
                if (Contains(candidate.Current.BoundingRectangle, point))
                {
                    if (list != null) { error = "目标文件列表不明确，请手动选择接收文件夹。"; return false; }
                    list = candidate;
                }
            }
            if (list == null) { error = "松开的位置不在可识别的文件列表内，请手动选择目录。"; return false; }
            itemBounds = ToRectangle(list.Current.BoundingRectangle);
            AutomationElementCollection children = list.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            for (int i = 0; i < children.Count; i++)
            {
                AutomationElement.AutomationElementInformation info = children[i].Current;
                if (info.IsOffscreen || !Contains(info.BoundingRectangle, point)) continue;
                ControlType type = info.ControlType;
                if (type == ControlType.Header || type == ControlType.HeaderItem || type == ControlType.ScrollBar || type == ControlType.Edit)
                { error = "请拖到文件夹的空白处或子文件夹图标，不能拖到标题栏、滚动条或重命名框。"; return false; }
                if (type == ControlType.ListItem || type == ControlType.DataItem)
                {
                    if (itemName != null && !String.Equals(itemName, info.Name, StringComparison.Ordinal))
                    { error = "鼠标下的文件夹项目不明确，请手动选择目录。"; return false; }
                    itemName = info.Name;
                    itemBounds = ToRectangle(info.BoundingRectangle);
                }
            }
            background = itemName == null;
            return true;
        }

        private static bool HitNativeList(IntPtr list, POINT point, out string itemName, out bool background,
            out System.Drawing.Rectangle itemBounds, out string error)
        {
            itemName = null;
            background = false;
            itemBounds = System.Drawing.Rectangle.Empty;
            error = null;
            RECT client;
            POINT origin = new POINT();
            if (!GetClientRect(list, out client) || !ClientToScreen(list, ref origin))
            { error = "无法读取文件列表位置，请手动选择目录。"; return false; }
            client.Left += origin.X; client.Right += origin.X; client.Top += origin.Y; client.Bottom += origin.Y;
            if (!client.Contains(point))
            { error = "请拖到文件夹内容区，不能拖到边框或滚动条。"; return false; }
            bool blocked = false;
            EnumChildWindows(list, delegate(IntPtr child, IntPtr unused)
            {
                RECT bounds;
                string name = WindowClass(child);
                if ((name == "SysHeader32" || name == "Edit" || name == "ScrollBar") && IsWindowVisible(child)
                    && GetWindowRect(child, out bounds) && bounds.Contains(point))
                { blocked = true; return false; }
                return true;
            }, IntPtr.Zero);
            if (blocked)
            { error = "请拖到文件夹的空白处或子文件夹图标，不能拖到标题栏、滚动条或重命名框。"; return false; }
            object accessible = null, hitObject = null;
            try
            {
                Guid iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
                int hr = AccessibleObjectFromWindow(list, 0xFFFFFFFC, ref iid, out accessible);
                if (hr < 0 || accessible == null)
                { error = "无法读取桌面图标，请手动选择接收目录。"; return false; }
                ConfigureProxy(accessible);
                object hit = Call(accessible, "accHitTest", point.X, point.Y);
                if (hit == null)
                { error = "鼠标未命中文件列表，请手动选择目录。"; return false; }
                object childId = 0;
                object item = accessible;
                if (hit is int)
                {
                    childId = hit;
                    if ((int)hit == 0)
                    {
                        background = true;
                        itemBounds = System.Drawing.Rectangle.FromLTRB(client.Left, client.Top, client.Right, client.Bottom);
                        return true;
                    }
                }
                else if (Marshal.IsComObject(hit))
                { hitObject = hit; item = hit; ConfigureProxy(item); }
                else { error = "无法识别鼠标下的桌面图标，请手动选择目录。"; return false; }

                int role = Convert.ToInt32(Dispatch(item, "accRole", BindingFlags.GetProperty, new object[] { childId }));
                int state = Convert.ToInt32(Dispatch(item, "accState", BindingFlags.GetProperty, new object[] { childId }));
                if (role != 34 || (state & 0x18001) != 0)
                { error = "鼠标下不是可用的文件夹项目，请手动选择目录。"; return false; }
                itemName = Convert.ToString(Dispatch(item, "accName", BindingFlags.GetProperty, new object[] { childId }));
                object[] args = { 0, 0, 0, 0, childId };
                ParameterModifier modifier = new ParameterModifier(5);
                modifier[0] = modifier[1] = modifier[2] = modifier[3] = true;
                item.GetType().InvokeMember("accLocation", BindingFlags.InvokeMethod, null, item, args,
                    new ParameterModifier[] { modifier }, null, null);
                int left = Convert.ToInt32(args[0]), top = Convert.ToInt32(args[1]);
                int width = Convert.ToInt32(args[2]), height = Convert.ToInt32(args[3]);
                if (String.IsNullOrWhiteSpace(itemName) || width <= 0 || height <= 0)
                { error = "无法确认文件夹图标的名称或位置，请手动选择目录。"; return false; }
                itemBounds = new System.Drawing.Rectangle(left, top, width, height);
                if (!itemBounds.Contains(point.X, point.Y))
                { error = "文件夹图标已经移动，请重新选择目录。"; return false; }
                return true;
            }
            finally { Release(hitObject); Release(accessible); }
        }

        private static System.Drawing.Rectangle ToRectangle(System.Windows.Rect value)
        {
            return value.IsEmpty ? System.Drawing.Rectangle.Empty : System.Drawing.Rectangle.FromLTRB(
                (int)Math.Floor(value.Left), (int)Math.Floor(value.Top), (int)Math.Ceiling(value.Right), (int)Math.Ceiling(value.Bottom));
        }

        internal static string NormalizeDirectory(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.StartsWith("::", StringComparison.Ordinal)
                    || path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)
                    || path.IndexOf("://", StringComparison.Ordinal) >= 0 || !Path.IsPathRooted(path)) return null;
                // Reject drive-relative paths such as C:foo and rooted-current-drive paths such as \foo.
                bool drive = path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
                bool unc = path.StartsWith("\\\\", StringComparison.Ordinal) && path.Trim('\\').Split('\\').Length >= 2;
                if (!drive && !unc) return null;
                string full = Path.GetFullPath(path);
                FileAttributes attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.Directory) == 0) return null;
                // A junction/symlink target may change after capture. Cloud placeholders
                // (OneDrive desktop/documents) keep their path and are accepted (v9.0.0.7).
                DirectoryInfo current = new DirectoryInfo(full);
                while (current != null)
                {
                    if (ReparsePolicy.Redirects(current)) return null;
                    current = current.Parent;
                }
                return full;
            }
            catch { return null; }
        }

        private static bool Contains(System.Windows.Rect r, POINT p)
        { return !r.IsEmpty && p.X >= r.Left && p.Y >= r.Top && p.X < r.Right && p.Y < r.Bottom; }

        private static IntPtr FindTopWindow(POINT point, IntPtr ignore)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                RECT bounds;
                if (window == ignore || window == ignoredPreviewWindow || window == ignoredDragIconWindow || !IsWindowVisible(window) || IsIconic(window) || !GetWindowRect(window, out bounds) || !bounds.Contains(point)) return true;
                int cloaked;
                if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static string WindowClass(IntPtr window)
        { var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString(); }
        private static string Fail(string error) { LastError = error; return null; }
        private static ResolvedTarget FailTarget(string error) { LastError = error; return null; }
        private static object Get(object obj, string name) { return Dispatch(obj, name, BindingFlags.GetProperty, new object[0]); }
        private static object Call(object obj, string name, params object[] args) { return Dispatch(obj, name, BindingFlags.InvokeMethod, args); }
        private static object Dispatch(object obj, string name, BindingFlags flags, object[] args)
        { return obj.GetType().InvokeMember(name, flags, null, obj, args); }
        private static object Track(List<object> objects, object value)
        {
            if (value != null)
            {
                objects.Add(value);
                ConfigureProxy(value);
            }
            return value;
        }
        private static void ConfigureProxy(object value)
        {
            // SYSTEM hosts impersonate Explorer's user. Dynamic cloaking propagates that identity to COM.
            if (value != null && Marshal.IsComObject(value))
            {
                IntPtr dispatch = IntPtr.Zero;
                try { dispatch = Marshal.GetIDispatchForObject(value); CoSetProxyBlanket(dispatch, UInt32.MaxValue, UInt32.MaxValue, IntPtr.Zero, 2, 3, IntPtr.Zero, 0x40); }
                catch (COMException) { }
                finally { if (dispatch != IntPtr.Zero) Marshal.Release(dispatch); }
            }
        }
        private static void Release(object value)
        { if (value != null && Marshal.IsComObject(value)) { try { Marshal.ReleaseComObject(value); } catch (InvalidComObjectException) { } } }

        private static IntPtr AncestorOfClass(IntPtr child, string className, IntPtr stop)
        {
            for (IntPtr current = GetParent(child); current != IntPtr.Zero && current != stop; current = GetParent(current))
                if (WindowClass(current) == className) return current;
            return IntPtr.Zero;
        }

        [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProviderNative { [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr result); }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OleWindowGetWindow(IntPtr self, out IntPtr window);
        private static readonly Guid IIDShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
        private static readonly Guid SIDTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");

        // The window of the tab (IShellBrowser derives from IOleWindow; GetWindow is
        // vtable slot 3). Zero when Explorer does not expose it; never throws.
        internal static IntPtr BrowserTabWindow(object browser)
        {
            IntPtr provider = IntPtr.Zero, shellBrowser = IntPtr.Zero;
            try
            {
                IServiceProviderNative services = browser as IServiceProviderNative;
                if (services == null) return IntPtr.Zero;
                provider = Marshal.GetComInterfaceForObject(browser, typeof(IServiceProviderNative));
                SetBlanket(provider);
                foreach (Guid service in new[] { SIDTopLevelBrowser, IIDShellBrowser })
                {
                    Guid sid = service, iid = IIDShellBrowser;
                    if (services.QueryService(ref sid, ref iid, out shellBrowser) == 0 && shellBrowser != IntPtr.Zero) break;
                    shellBrowser = IntPtr.Zero;
                }
                if (shellBrowser == IntPtr.Zero) return IntPtr.Zero;
                SetBlanket(shellBrowser);
                IntPtr table = Marshal.ReadIntPtr(shellBrowser);
                OleWindowGetWindow getWindow = (OleWindowGetWindow)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, 3 * IntPtr.Size), typeof(OleWindowGetWindow));
                IntPtr result;
                return getWindow(shellBrowser, out result) == 0 ? result : IntPtr.Zero;
            }
            catch (Exception) { return IntPtr.Zero; }
            finally
            {
                if (shellBrowser != IntPtr.Zero) Marshal.Release(shellBrowser);
                if (provider != IntPtr.Zero) Marshal.Release(provider);
            }
        }
        private static void SetBlanket(IntPtr proxy)
        { if (proxy != IntPtr.Zero) CoSetProxyBlanket(proxy, UInt32.MaxValue, UInt32.MaxValue, IntPtr.Zero, 2, 3, IntPtr.Zero, 0x40); }

        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT
        {
            public int Left, Top, Right, Bottom;
            public bool Contains(POINT point) { return point.X >= Left && point.Y >= Top && point.X < Right && point.Y < Bottom; }
        }
        private delegate bool EnumProc(IntPtr window, IntPtr param);
        [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("ole32.dll")] private static extern int CoSetProxyBlanket(IntPtr proxy, uint authentication, uint authorization, IntPtr principal, uint authenticationLevel, uint impersonationLevel, IntPtr identity, uint capabilities);
        [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object accessible);
    }

    // v9.0.0.7: which reparse points may change what a path refers to.
    // Cloud-file placeholders (OneDrive "Files On-Demand" desktop/documents), data
    // deduplication and WOF compression keep their path and are accepted. Mount
    // points, symbolic links, app-exec links and any unknown tag stay rejected.
    internal static class ReparsePolicy
    {
        internal static bool Redirects(FileSystemInfo item)
        {
            item.Refresh();
            if ((item.Attributes & FileAttributes.ReparsePoint) == 0) return false;
            uint tag;
            return !TryTag(item.FullName, out tag) || !Benign(tag);
        }
        internal static bool Benign(uint tag)
        {
            return (tag & 0xFFFF0FFF) == 0x9000001A   // IO_REPARSE_TAG_CLOUD and CLOUD_1..F
                || tag == 0x80000021                 // IO_REPARSE_TAG_ONEDRIVE (legacy)
                || tag == 0x80000013                 // IO_REPARSE_TAG_DEDUP
                || tag == 0x80000017;                // IO_REPARSE_TAG_WOF
        }
        static bool TryTag(string path, out uint tag)
        {
            tag = 0;
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 0 || trimmed.IndexOfAny(new[] { '*', '?' }) >= 0) return false;
            FindData data;
            IntPtr handle = FindFirstFileW(trimmed, out data);
            if (handle == new IntPtr(-1)) return false;
            FindClose(handle);
            if ((data.Attributes & 0x400) == 0) return false;
            tag = data.Reserved0; return true;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FindData
        {
            // FILETIME is two DWORDs (4-byte aligned); a long would add padding.
            public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFileW(string path, out FindData data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr handle);
    }
}
