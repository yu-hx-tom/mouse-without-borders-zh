using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using MwbTransfer;

class DropTargetTests
{
    static int assertions;
    static Exception failure;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); assertions++; }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--desktop-readonly") return ProbeDesktopNamespace();
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drop-target-fixture");
            Directory.CreateDirectory(directory);
            string child = Path.Combine(directory, "合成目录");
            Directory.CreateDirectory(child);
            string file = Path.Combine(directory, "synthetic.txt");
            File.WriteAllText(file, "Synthetic test file only.");
            Check(DropTarget.NormalizeDirectory(directory) == Path.GetFullPath(directory), "existing directory");
            Check(DropTarget.NormalizeDirectory(child) == Path.GetFullPath(child), "Chinese directory");
            Check(DropTarget.NormalizeDirectory(file) == null, "file is not directory");
            Check(DropTarget.NormalizeDirectory(Path.Combine(directory, "missing")) == null, "missing directory");
            foreach (string bad in new string[] { null, "", " ", "relative", "C:relative", "\\relative", "::{GUID}", "shell:Downloads", "file:///C:/Windows", "https://example.invalid", "\\\\?\\C:\\Windows", "\\\\.\\C:\\Windows", "C:\\bad\0path", "C:\\bad<path", "\\\\server" })
                Check(DropTarget.NormalizeDirectory(bad) == null, "invalid/native boundary " + bad);

            string userDesktop = Path.Combine(directory, "synthetic-user-desktop");
            string publicDesktop = Path.Combine(directory, "synthetic-public-desktop");
            Directory.CreateDirectory(userDesktop);
            Directory.CreateDirectory(publicDesktop);
            string error;
            var userItem = new DropTarget.ShellItem { Name = "用户目录图标", Path = userDesktop, IsFolder = true, IsFileSystem = true };
            var publicItem = new DropTarget.ShellItem { Name = "公共目录图标", Path = publicDesktop, IsFolder = true, IsFileSystem = true };
            var merged = new[] { userItem, publicItem };
            Check(DropTarget.ResolveItem(merged, userItem.Name, true, out error) == userDesktop, "merged user desktop mapping");
            Check(DropTarget.ResolveItem(merged, publicItem.Name, true, out error) == publicDesktop, "merged public desktop mapping");
            Check(DropTarget.ResolveItem(merged, "不存在的显示名称", true, out error) == null && error != null, "never guess filesystem path from icon name");
            Check(DropTarget.ResolveItem(merged, null, true, out error) == null && error != null, "desktop blank is explicit fallback");
            var duplicate = new DropTarget.ShellItem { Name = userItem.Name, Path = publicDesktop, IsFolder = true, IsFileSystem = true };
            Check(DropTarget.ResolveItem(new[] { userItem, duplicate }, userItem.Name, true, out error) == null && error.Contains("同名"), "user/public duplicate label rejected");
            var virtualItem = new DropTarget.ShellItem { Name = "此电脑", Path = userDesktop, IsFolder = true, IsFileSystem = false };
            Check(DropTarget.ResolveItem(new[] { virtualItem }, virtualItem.Name, true, out error) == null, "virtual desktop item rejected even if path looks physical");
            virtualItem.Name = userItem.Name;
            Check(DropTarget.ResolveItem(new[] { userItem, virtualItem }, userItem.Name, true, out error) == null, "physical/virtual duplicate label rejected");
            var shortcut = new DropTarget.ShellItem { Name = "文件夹快捷方式", Path = userDesktop, IsFolder = true, IsFileSystem = true, IsLink = true };
            Check(DropTarget.ResolveItem(new[] { shortcut }, shortcut.Name, true, out error) == null, "desktop folder shortcut rejected");
            var fileItem = new DropTarget.ShellItem { Name = "文件", Path = file, IsFolder = false, IsFileSystem = true };
            Check(DropTarget.ResolveItem(new[] { fileItem }, fileItem.Name, true, out error) == null, "desktop file icon rejected");
            var networkItem = new DropTarget.ShellItem { Name = "网络目录", Path = @"\\invalid-probe-host\share", IsFolder = true, IsFileSystem = true };
            Check(DropTarget.ResolveItem(new[] { networkItem }, networkItem.Name, true, out error) == null, "desktop UNC rejected without probing network");
            Check(DropTarget.ResolveItem(merged, publicItem.Name, false, out error) == publicDesktop, "Explorer folder mapping retained");
            Check(DropTarget.IsDesktopHost("Progman"), "Progman host accepted");
            Check(DropTarget.IsDesktopHost("WorkerW"), "WorkerW host accepted");
            Check(!DropTarget.IsDesktopHost("Shell_TrayWnd"), "taskbar not a desktop drop target");

            using (Form form = new Form())
            using (ImageList icons = new ImageList())
            {
                form.Text = "MWB synthetic drop-target test";
                form.ShowInTaskbar = false; form.TopMost = true; // v9.0.0.10 (Codex 9.0.0.9 note): fixture must not depend on other apps' z-order.
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(24, 24);
                form.ClientSize = new Size(860, 270);
                Panel panel = new Panel { Dock = DockStyle.Fill };
                ListView list = new ListView { View = View.Details, Bounds = new Rectangle(10, 10, 400, 230) };
                list.Columns.Add("Synthetic name", 260);
                list.Items.Add("合成子目录");
                panel.Controls.Add(list);
                icons.ImageSize = new Size(32, 32);
                icons.Images.Add(SystemIcons.Application);
                ListView desktop = new ListView { View = View.LargeIcon, Bounds = new Rectangle(430, 10, 400, 230), LargeImageList = icons };
                desktop.Items.Add("合成桌面目录", 0);
                panel.Controls.Add(desktop);
                form.Controls.Add(panel);
                form.Shown += delegate
                {
                    IntPtr view = panel.Handle;
                    IntPtr nativeList = list.Handle, nativeDesktop = desktop.Handle;
                    Rectangle itemBounds = list.Items[0].Bounds;
                    Point itemPoint = list.PointToScreen(new Point(itemBounds.Left + 25, itemBounds.Top + itemBounds.Height / 2));
                    Point blankPoint = list.PointToScreen(new Point(60, 170));
                    Point headerPoint = list.PointToScreen(new Point(60, 8));
                    Rectangle icon = desktop.Items[0].GetBounds(ItemBoundsPortion.Icon);
                    Rectangle label = desktop.Items[0].GetBounds(ItemBoundsPortion.Label);
                    Point iconPoint = desktop.PointToScreen(new Point(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2));
                    Point labelPoint = desktop.PointToScreen(new Point(label.Left + label.Width / 2, label.Top + label.Height / 2));
                    Point desktopBlank = desktop.PointToScreen(new Point(250, 170));
                    Thread probe = new Thread(delegate()
                    {
                        try
                        {
                            Thread.Sleep(250);
                            CheckHit(view, itemPoint, true, false, "合成子目录");
                            CheckHit(view, blankPoint, true, true, null);
                            CheckHit(view, headerPoint, false, false, null);
                            CheckHit(view, iconPoint, true, false, "合成桌面目录");
                            CheckHit(view, labelPoint, true, false, "合成桌面目录");
                            CheckHit(view, desktopBlank, true, true, null);
                            // Windows 11 can expose the list at the root of the scoped UIA tree.
                            CheckHit(nativeDesktop, iconPoint, true, false, "合成桌面目录");
                            CheckHit(nativeList, blankPoint, true, true, null);
                            CheckNativeHit(nativeDesktop, iconPoint, true, false, "合成桌面目录");
                            CheckNativeHit(nativeDesktop, labelPoint, true, false, "合成桌面目录");
                            CheckNativeHit(nativeDesktop, desktopBlank, true, true, null);
                            CheckNativeHit(nativeList, itemPoint, true, false, "合成子目录");
                            CheckNativeHit(nativeList, headerPoint, false, false, null);
                            Form overlay = null;
                            form.Invoke((Action)delegate {
                                overlay = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                                    StartPosition = FormStartPosition.Manual, Bounds = form.Bounds, TopMost = true };
                                overlay.Show(form);
                                DropTarget.IgnorePreviewWindow(overlay.Handle);
                            });
                            try
                            {
                                // Hit testing must address the underlying list, even behind the drag overlay.
                                CheckNativeHit(nativeDesktop, iconPoint, true, false, "合成桌面目录");
                                CheckNativeHit(nativeDesktop, labelPoint, true, false, "合成桌面目录");
                                CheckNativeHit(nativeList, itemPoint, true, false, "合成子目录");
                                MethodInfo top = typeof(DropTarget).GetMethod("FindTopWindow", BindingFlags.Static | BindingFlags.NonPublic);
                                IntPtr found = (IntPtr)top.Invoke(null, new object[] { new DropTarget.POINT { X = iconPoint.X, Y = iconPoint.Y }, IntPtr.Zero });
                                Check(found == form.Handle, "preview window excluded from capture");
                            }
                            finally
                            {
                                form.Invoke((Action)delegate { DropTarget.IgnorePreviewWindow(IntPtr.Zero); overlay.Dispose(); });
                            }
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.BeginInvoke((Action)delegate { form.Close(); }); }
                    });
                    probe.IsBackground = true;
                    probe.SetApartmentState(ApartmentState.STA);
                    probe.Start();
                };
                Application.Run(form);
            }
            if (failure != null) throw failure;
            Console.WriteLine("PASS " + assertions + " checks: path guards, desktop mapping, root-inclusive UIA, direct native MSAA item bounds, and covered icon/label hits. No Explorer or user files queried; actual Windows 11 desktop not tested.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    // Optional real-shell metadata probe. Does not invoke/select/move icons, create desktop files,
    // set cursor position, or output item names/paths. It is separate from the synthetic tests.
    static int ProbeDesktopNamespace()
    {
        object shell = null, desktop = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
            desktop = shell.GetType().InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { 0 });
            MethodInfo read = typeof(DropTarget).GetMethod("ReadFolderItems", BindingFlags.Static | BindingFlags.NonPublic);
            var items = (List<DropTarget.ShellItem>)read.Invoke(null, new object[] { desktop });
            int physicalFolders = 0, links = 0, virtualItems = 0, duplicateLabels = 0;
            var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (DropTarget.ShellItem item in items)
            {
                if (item.IsFileSystem && item.IsFolder && !item.IsLink) physicalFolders++;
                if (item.IsLink) links++;
                if (!item.IsFileSystem) virtualItems++;
                int count;
                labels.TryGetValue(item.Name, out count);
                labels[item.Name] = count + 1;
            }
            foreach (int count in labels.Values) if (count > 1) duplicateLabels++;
            Console.WriteLine("DesktopNamespaceRead=True; Items=" + items.Count + "; PhysicalFolderIcons=" + physicalFolders + "; Links=" + links + "; Virtual=" + virtualItems + "; DuplicateLabels=" + duplicateLabels);
            return 0;
        }
        finally
        {
            if (desktop != null && Marshal.IsComObject(desktop)) Marshal.ReleaseComObject(desktop);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    static void CheckHit(IntPtr view, Point point, bool expected, bool background, string item)
    {
        MethodInfo hit = typeof(DropTarget).GetMethod("HitFileList", BindingFlags.NonPublic | BindingFlags.Static);
        var p = new DropTarget.POINT { X = point.X, Y = point.Y };
        object[] args = { view, p, null, false, null };
        bool ok = (bool)hit.Invoke(null, args);
        Check(ok == expected, "hit result: " + args[4]);
        if (ok)
        {
            Check((bool)args[3] == background, "hit background");
            Check((string)args[2] == item, "hit item: " + args[2]);
        }
    }

    static void CheckNativeHit(IntPtr view, Point point, bool expected, bool background, string item)
    {
        MethodInfo hit = typeof(DropTarget).GetMethod("HitNativeList", BindingFlags.NonPublic | BindingFlags.Static);
        var p = new DropTarget.POINT { X = point.X, Y = point.Y };
        object[] args = { view, p, null, false, Rectangle.Empty, null };
        bool ok = (bool)hit.Invoke(null, args);
        Check(ok == expected, "native hit result: " + args[5]);
        if (ok)
        {
            Check((bool)args[3] == background, "native hit background");
            Check((string)args[2] == item, "native hit item: " + args[2]);
            Check(((Rectangle)args[4]).Contains(point), "native target rectangle contains point");
        }
    }
}
