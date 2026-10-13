using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MwbTransferSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Self-test does file transactions only in its own fresh temporary directory.
            if (args.Length == 1 && args[0] == "--self-test") return Installer.SelfTest();
            bool commandLine = args.Length == 1 && (args[0] == "--install" || args[0] == "--restore");
            if (args.Length > 0 && !commandLine) { Console.Error.WriteLine("Usage: [--install | --restore | --self-test]"); return 2; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Mutex mutex = new Mutex(false, "Global\\MwbTransferUpgradeInstaller"))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                    if (!held) { if (commandLine) Console.Error.WriteLine("另一个升级窗口正在运行。"); else MessageBox.Show("另一个升级窗口正在运行。", "无界鼠标"); return 2; }
                    if (commandLine)
                    {
                        try { new Installer(Console.WriteLine).Run(args[0] == "--restore"); Console.WriteLine("SETUP_SUCCESS"); return 0; }
                        catch (Exception ex) { Console.Error.WriteLine("SETUP_FAILED: " + ex.Message); return 1; }
                    }
                    Application.Run(new SetupForm());
                    return 0;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly Button install = new Button { Text = "安装 / 升级", Width = 136, Height = 38 };
        private readonly Button restore = new Button { Text = "恢复升级前版本", Width = 152, Height = 38 };
        private readonly TextBox status = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        private bool busy;
        public SetupForm()
        {
            bool native = Assembly.GetExecutingAssembly().GetName().Version.Major >= 9;
            Text = "无界鼠标 · " + (native ? "原生拖拽测试版 v" : "连续拖拽与简洁边框版 v") + Assembly.GetExecutingAssembly().GetName().Version.Major;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(247, 249, 246);
            ClientSize = new Size(646, 444);
            MinimumSize = new Size(662, 483);
            StartPosition = FormStartPosition.CenterScreen;
            Label title = new Label { Text = native ? "原生拖拽恢复 · 双机测试版" : "连续跨屏拖拽与简洁边框", AutoSize = true, Font = new Font(Font.FontFamily, 20f, FontStyle.Bold), Location = new Point(26, 25) };
            Label detail = new Label { Text = "两台电脑都安装此升级包。保留现有配对与设置。\r\n适用于独立版 Mouse without Borders 2.2.1.0327。", AutoSize = false, Location = new Point(28, 77), Size = new Size(586, 50) };
            install.Location = new Point(28, 137); install.BackColor = Color.FromArgb(34, 83, 63); install.ForeColor = Color.White;
            restore.Location = new Point(178, 137);
            status.SetBounds(28, 196, 590, 220); status.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.AddRange(new Control[] { title, detail, install, restore, status });
            install.Click += delegate { Run(false); };
            restore.Click += delegate { Run(true); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            Log("准备就绪。升级期间共享鼠标和键盘将短暂断开。");
            Log("本包为本地定制升级，不是微软官方发布。无需重新配对。");
        }
        private void Log(string value)
        {
            if (InvokeRequired) { BeginInvoke((Action<string>)Log, value); return; }
            status.AppendText(value + Environment.NewLine);
        }
        private void Run(bool rollback)
        {
            busy = true; install.Enabled = restore.Enabled = false;
            Thread worker = new Thread(delegate()
            {
                try { new Installer(Log).Run(rollback); Log(rollback ? "恢复完成。共享服务已启动。" : "升级完成。请在另一台电脑安装同一个升级包，再进行跨屏文件拖放。"); }
                catch (Exception ex) { Log("未完成：" + ex.Message); }
                finally { BeginInvoke((Action)delegate { busy = false; install.Enabled = restore.Enabled = true; }); }
            });
            worker.IsBackground = true;
            worker.Start();
        }
    }

    internal sealed class Installer
    {
        private const string ServiceName = "MouseWithoutBordersSvc";
        private static readonly string[] Files = Assembly.GetExecutingAssembly().GetName().Version.Major >= 9
            ? new[] { "MouseWithoutBorders.exe", "MwbModernUi.dll", "MwbTransfer.dll", "MouseWithoutBordersHelper.exe", "MwbNativeDrag.dll" }
            : new[] { "MouseWithoutBorders.exe", "MwbModernUi.dll", "MwbTransfer.dll" };
        private static readonly string[] PairValues = { "MachineId", "MachinePool", "MachineMatrix", "FirstRun" };
        private readonly Action<string> log;
        private string installDir;
        private string journalPath;
        internal Installer(Action<string> logger)
        {
            log = delegate(string message)
            {
                logger(message);
                if (journalPath == null) return;
                // A log error must not turn a successful recovery into a second installation failure.
                try { File.AppendAllText(journalPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, Encoding.UTF8); } catch { }
            };
        }

        internal void Run(bool restore)
        {
            try { RunCore(restore); log(restore ? "SETUP_SUCCESS: restore" : "SETUP_SUCCESS: install"); }
            catch (Exception ex) { log("SETUP_FAILED: " + ex.ToString()); throw; }
        }

        private void RunCore(bool restore)
        {
            Dictionary<string, string> manifest = ReadManifest(ResourceText("payload.manifest"));
            Dictionary<string, string> metadata = ReadManifest(ResourceText("setup.metadata"));
            Dictionary<string, byte[]> payload = LoadPayload(manifest);
            installDir = FindInstallation(metadata);
            string backupRoot = Path.Combine(installDir, "MwbTransferUpgradeBackups");
            CheckPath(backupRoot); Directory.CreateDirectory(backupRoot);
            journalPath = Path.Combine(backupRoot, "installer.log"); CheckPath(journalPath);
            log(restore ? "开始恢复升级前版本" : "开始安装 v" + Assembly.GetExecutingAssembly().GetName().Version);
            log("已找到独立版：" + installDir);
            string current = HashFile(Path.Combine(installDir, Files[0]));
            HashSet<string> allowed = new HashSet<string>(metadata["AcceptedExeHashes"].Split(','), StringComparer.OrdinalIgnoreCase);
            allowed.Add(manifest[Files[0]]);
            if (!allowed.Contains(current)) throw new InvalidOperationException("已安装的程序与本升级包支持的版本不一致。为保留现有程序，未修改任何文件。");
            if (AssemblyName.GetAssemblyName(Path.Combine(installDir, Files[0])).Version != new Version(2, 2, 1, 327))
                throw new InvalidOperationException("仅支持独立版 2.2.1.0327。");
            CheckPath(backupRoot);
            Dictionary<string, string> pairBefore = PairSnapshot();
            if (restore)
            {
                string marker = Path.Combine(backupRoot, "latest.txt");
                CheckPath(marker);
                if (!File.Exists(marker)) throw new InvalidOperationException("未找到此升级包创建的备份。");
                string id = File.ReadAllText(marker).Trim();
                if (id.Length != 48 || id.Any(c => !(Char.IsDigit(c) || c == '-' || (c >= 'a' && c <= 'f'))))
                    throw new InvalidOperationException("备份记录格式不正确，未恢复。");
                string prior = Path.Combine(backupRoot, id);
                VerifyBackup(prior);
                // Back up the current working version before every restore, too.
                string safety = CreateBackup(installDir, backupRoot);
                RunTransaction(delegate { RestoreFiles(prior, installDir); }, delegate { RestoreFiles(safety, installDir); }, pairBefore);
                CheckPath(marker); File.WriteAllText(marker, Path.GetFileName(safety));
                log("恢复来源：" + prior);
                return;
            }
            if (Files.All(name => File.Exists(Path.Combine(installDir, name)) && String.Equals(HashFile(Path.Combine(installDir, name)), manifest[name], StringComparison.OrdinalIgnoreCase)))
            {
                log("此版本已经安装，文件校验通过。");
                StartAndVerify();
                return;
            }
            string backup = current == manifest[Files[0]] ? FindRepairBackup(backupRoot, current, allowed) : null;
            if (backup == null) backup = CreateBackup(installDir, backupRoot);
            // 9.0.0.x builds share one main executable, so a normal 9.0.0.x upgrade
            // also takes this branch. The retained backup is the pre-v9 version.
            else log("主程序与本版本相同（9.0.0.x 之间升级，或上次安装未完成）：沿用此前完整版本的恢复备份；安装失败或执行“恢复”时将回到该版本。");
            log("已保存恢复备份：" + backup);
            RunTransaction(delegate { WritePayload(installDir, payload, manifest); }, delegate { RestoreFiles(backup, installDir); }, pairBefore);
            VerifyInstalledFiles(installDir, manifest);
            string latest = Path.Combine(backupRoot, "latest.txt");
            CheckPath(latest); File.WriteAllText(latest, Path.GetFileName(backup));
            log(Files.Length + " 个程序文件校验通过，原配对信息保持一致。");
        }

        private void RunTransaction(Action change, Action rollback, Dictionary<string, string> pairBefore)
        {
            bool stopped = false;
            try
            {
                log("正在暂停共享服务……");
                StopExactProcesses(); stopped = true;
                change();
                log("正在重新启动共享服务……");
                StartAndVerify();
                if (!SnapshotsEqual(pairBefore, PairSnapshot())) throw new InvalidOperationException("检测到配对记录发生变化。");
            }
            catch (Exception failure)
            {
                if (!stopped) { try { StartAndVerify(); } catch { } throw; }
                log("安装验证未通过，正在恢复原程序……");
                try { StopExactProcesses(); rollback(); StartAndVerify(); }
                catch (Exception recovery) { throw new InvalidOperationException(failure.Message + "；自动恢复未完成：" + recovery.Message, new AggregateException(failure, recovery)); }
                throw new InvalidOperationException(failure.Message + "；已恢复升级前程序。", failure);
            }
        }

        private static string FindInstallation(Dictionary<string, string> metadata)
        {
            using (ManagementObjectSearcher search = new ManagementObjectSearcher("SELECT PathName FROM Win32_Service WHERE Name='" + ServiceName + "'"))
            using (ManagementObjectCollection rows = search.Get())
            {
                string command = null;
                foreach (ManagementObject row in rows) { command = Convert.ToString(row["PathName"]).Trim(); break; }
                if (String.IsNullOrEmpty(command)) throw new InvalidOperationException("未找到独立版无界鼠标。请先安装微软官方 Mouse without Borders 2.2.1.0327，再运行此升级包。");
                string serviceExe = command;
                if (command.StartsWith("\""))
                {
                    int end = command.IndexOf('"', 1);
                    if (end < 0 || command.Substring(end + 1).Trim().Length != 0) throw new InvalidOperationException("独立服务路径格式不匹配。");
                    serviceExe = command.Substring(1, end - 1);
                }
                serviceExe = Path.GetFullPath(Environment.ExpandEnvironmentVariables(serviceExe));
                if (!String.Equals(Path.GetFileName(serviceExe), "MouseWithoutBordersSvc.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("服务不是受支持的独立版。");
                string dir = Path.GetDirectoryName(serviceExe);
                CheckPath(dir);
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (String.IsNullOrEmpty(programFiles)) programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                // Backups inherit Program Files ACLs; never place elevated backup journals in a user-writable install folder.
                if (!dir.StartsWith(Path.GetFullPath(programFiles).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("安装位置不是受保护的 Program Files 目录。本升级包未更改程序；请在标准目录安装官方独立版。");
                foreach (string file in Files) CheckPath(Path.Combine(dir, file));
                foreach (string file in new[] { "MouseWithoutBordersSvc.exe", "MouseWithoutBordersHelper.exe" })
                {
                    string path = Path.Combine(dir, file); CheckPath(path);
                    if (!File.Exists(path) || !metadata[file].Split(',').Contains(HashFile(path), StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("独立版服务或辅助程序版本不匹配：" + file);
                }
                return dir;
            }
        }

        private void StopExactProcesses()
        {
            using (ServiceController service = new ServiceController(ServiceName))
            {
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25)); }
            }
            log("独立服务已停止，正在结束对应主进程及辅助进程。");
            string main = Path.Combine(installDir, Files[0]);
            string helper = Path.Combine(installDir, "MouseWithoutBordersHelper.exe");
            // Include the WMI key (Handle): projected rows without it have no instance path and cannot invoke Terminate.
            using (ManagementObjectSearcher search = new ManagementObjectSearcher("SELECT * FROM Win32_Process WHERE Name='MouseWithoutBorders.exe' OR Name='MouseWithoutBordersHelper.exe'"))
            using (ManagementObjectCollection processes = search.Get())
            {
                foreach (ManagementObject process in processes)
                {
                    string path = Convert.ToString(process["ExecutablePath"]);
                    if (!String.Equals(path, main, StringComparison.OrdinalIgnoreCase) && !String.Equals(path, helper, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (process.Path == null || String.IsNullOrEmpty(process.Path.Path)) throw new InvalidOperationException("无法确定独立进程的 WMI 实例路径。");
                        log("结束独立进程 PID " + Convert.ToString(process["ProcessId"]) + "：" + Path.GetFileName(path));
                        object result = process.InvokeMethod("Terminate", new object[] { 0 });
                        if (Convert.ToUInt32(result) != 0) throw new InvalidOperationException("无法结束独立版共享进程。");
                    }
                    catch (ManagementException ex) { if (ex.ErrorCode != ManagementStatus.NotFound) throw; }
                }
            }
            DateTime end = DateTime.UtcNow.AddSeconds(10);
            while (HasExactMainProcess())
            {
                if (DateTime.UtcNow > end) throw new InvalidOperationException("原程序仍在运行，未替换程序文件。");
                Thread.Sleep(200);
            }
        }

        private void StartAndVerify()
        {
            using (ServiceController service = new ServiceController(ServiceName))
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
                if (service.Status == ServiceControllerStatus.Stopped) service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
            }
            log("独立服务已运行，正在检查主程序。");
            DateTime end = DateTime.UtcNow.AddSeconds(12);
            while (!HasExactMainProcess())
            {
                if (DateTime.UtcNow > end) throw new InvalidOperationException("服务启动后未检测到独立版主程序。");
                Thread.Sleep(400);
            }
            Thread.Sleep(1500);
            if (!HasExactMainProcess()) throw new InvalidOperationException("主程序启动后意外退出。");
        }
        private bool HasExactMainProcess()
        {
            string expected = Path.Combine(installDir, Files[0]);
            using (ManagementObjectSearcher search = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE Name='MouseWithoutBorders.exe'"))
            using (ManagementObjectCollection processes = search.Get())
                foreach (ManagementObject process in processes) if (String.Equals(Convert.ToString(process["ExecutablePath"]), expected, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static Dictionary<string, string> PairSnapshot()
        {
            Dictionary<string, string> snapshot = new Dictionary<string, string>();
            foreach (string prefix in new[] { ".DEFAULT", System.Security.Principal.WindowsIdentity.GetCurrent().User.Value })
            using (RegistryKey key = Registry.Users.OpenSubKey(prefix + "\\SOFTWARE\\Microsoft\\MouseWithoutBorders", false))
            {
                foreach (string name in PairValues)
                {
                    object value = key == null ? null : key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string encoded = value == null ? "<absent>" : value is byte[] ? Convert.ToBase64String((byte[])value) : value is string[] ? String.Join("\0", (string[])value) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    snapshot[prefix + "\\" + name] = (value == null ? "" : value.GetType().FullName + ":") + encoded;
                }
            }
            return snapshot;
        }
        private static bool SnapshotsEqual(Dictionary<string, string> a, Dictionary<string, string> b) { return a.Count == b.Count && a.All(x => b.ContainsKey(x.Key) && x.Value == b[x.Key]); }

        private static string CreateBackup(string source, string root)
        {
            CheckPath(root); Directory.CreateDirectory(root);
            string dir = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            StringBuilder manifest = new StringBuilder();
            foreach (string name in Files)
            {
                string path = Path.Combine(source, name); CheckPath(path);
                if (!File.Exists(path)) { manifest.AppendLine(name + "|ABSENT"); continue; }
                string dest = Path.Combine(dir, name); File.Copy(path, dest, false);
                string hash = HashFile(path);
                if (HashFile(dest) != hash) throw new InvalidOperationException("备份校验失败：" + name);
                manifest.AppendLine(name + "|" + hash);
            }
            File.WriteAllText(Path.Combine(dir, "backup.manifest"), manifest.ToString(), Encoding.UTF8);
            VerifyBackup(dir);
            return dir;
        }
        private static Dictionary<string, string> VerifyBackup(string directory)
        {
            CheckPath(directory); CheckPath(Path.Combine(directory, "backup.manifest"));
            Dictionary<string, string> manifest = ReadManifest(File.ReadAllText(Path.Combine(directory, "backup.manifest")));
            if (manifest.Count != Files.Length || Files.Any(n => !manifest.ContainsKey(n)) || manifest[Files[0]] == "ABSENT") throw new InvalidOperationException("备份记录不完整。");
            foreach (string name in Files)
            {
                if (manifest[name] == "ABSENT") continue;
                string path = Path.Combine(directory, name); CheckPath(path);
                if (!File.Exists(path) || !String.Equals(HashFile(path), manifest[name], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("备份文件校验失败：" + name);
            }
            return manifest;
        }
        private static void RestoreFiles(string backup, string destination)
        {
            Dictionary<string, string> manifest = VerifyBackup(backup);
            foreach (string name in Files)
            {
                string path = Path.Combine(destination, name); CheckPath(path);
                // Only the fixed payload names can be removed, when the verified
                // pre-install backup says they did not exist.
                if (manifest[name] == "ABSENT") { if (File.Exists(path)) File.Delete(path); continue; }
                File.Copy(Path.Combine(backup, name), path, true);
                if (!String.Equals(HashFile(path), manifest[name], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("恢复文件校验失败：" + name);
            }
        }
        private static void WritePayload(string destination, Dictionary<string, byte[]> payload, Dictionary<string, string> manifest)
        {
            // Dependencies first, executable last. Caller has stopped the service and can restore the complete backup.
            foreach (string name in Files.Skip(1).Concat(new[] { Files[0] }))
            {
                string path = Path.Combine(destination, name); CheckPath(path);
                File.WriteAllBytes(path, payload[name]);
                if (!String.Equals(HashFile(path), manifest[name], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("安装文件校验失败：" + name);
            }
            VerifyInstalledFiles(destination, manifest);
        }
        private static void VerifyInstalledFiles(string directory, Dictionary<string, string> manifest)
        {
            foreach (string name in Files)
            {
                string path = Path.Combine(directory, name); CheckPath(path);
                if (!File.Exists(path) || !String.Equals(HashFile(path), manifest[name], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("安装不完整或文件不匹配：" + name);
            }
        }
        private static string FindRepairBackup(string root, string current, HashSet<string> allowed)
        {
            string marker = Path.Combine(root, "latest.txt"); CheckPath(marker);
            if (!File.Exists(marker)) return null;
            string id = File.ReadAllText(marker).Trim();
            if (id.Length != 48 || id.Any(c => !(Char.IsDigit(c) || c == '-' || (c >= 'a' && c <= 'f'))))
                throw new InvalidOperationException("恢复备份编号无效，未覆盖原备份。");
            string prior = Path.Combine(root, id);
            Dictionary<string, string> backup = VerifyBackup(prior);
            return backup[Files[0]] != current && allowed.Contains(backup[Files[0]]) ? prior : null;
        }
        private static Dictionary<string, byte[]> LoadPayload(Dictionary<string, string> manifest)
        {
            if (manifest.Count != Files.Length || Files.Any(n => !manifest.ContainsKey(n))) throw new InvalidOperationException("安装包文件清单无效。");
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
            foreach (string name in Files)
            {
                byte[] bytes = ResourceBytes("payload." + name);
                if (!String.Equals(HashBytes(bytes), manifest[name], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("安装包完整性校验失败：" + name);
                files.Add(name, bytes);
            }
            return files;
        }
        private static Dictionary<string, string> ReadManifest(string text)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in text.Trim('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int split = raw.IndexOf('|');
                if (split <= 0 || split == raw.Length - 1) throw new InvalidOperationException("安装包清单格式错误。");
                result.Add(raw.Substring(0, split), raw.Substring(split + 1));
            }
            return result;
        }
        private static byte[] ResourceBytes(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidOperationException("安装包缺少资源：" + name);
                using (MemoryStream output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); }
            }
        }
        private static string ResourceText(string name) { return Encoding.UTF8.GetString(ResourceBytes(name)); }
        private static string HashFile(string path) { using (FileStream stream = File.OpenRead(path)) using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
        private static string HashBytes(byte[] data) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""); }
        private static void CheckPath(string path)
        {
            string next = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(next))
            {
                if ((File.Exists(next) || Directory.Exists(next)) && (File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("路径包含链接或重解析点，未修改：" + next);
                string parent = Path.GetDirectoryName(next);
                if (String.Equals(next, parent, StringComparison.OrdinalIgnoreCase)) break;
                next = parent;
            }
        }

        internal static int SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "MwbSetupSelfTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Dictionary<string, string> manifest = ReadManifest(ResourceText("payload.manifest"));
                Dictionary<string, byte[]> payload = LoadPayload(manifest);
                Dictionary<string, string> metadata = ReadManifest(ResourceText("setup.metadata"));
                string[] acceptedHashes = metadata["AcceptedExeHashes"].Split(',');
                if (metadata.Count != 3 || acceptedHashes.Length < 3 || acceptedHashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != acceptedHashes.Length || acceptedHashes.Any(hash => hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) || metadata["MouseWithoutBordersSvc.exe"].Length != 64 || metadata["MouseWithoutBordersHelper.exe"].Split(',').Any(hash => hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))) throw new Exception("Metadata mismatch");
                bool badPayloadRejected = false;
                Dictionary<string, string> badManifest = new Dictionary<string, string>(manifest);
                badManifest[Files[2]] = new string('0', 64);
                try { LoadPayload(badManifest); } catch (InvalidOperationException) { badPayloadRejected = true; }
                if (!badPayloadRejected) throw new Exception("Tampered payload digest accepted");
                string target = Path.Combine(root, "install"); Directory.CreateDirectory(target);
                byte[] oldExe = Encoding.UTF8.GetBytes("original-exe");
                byte[] oldUi = Encoding.UTF8.GetBytes("original-ui");
                File.WriteAllBytes(Path.Combine(target, Files[0]), oldExe);
                File.WriteAllBytes(Path.Combine(target, Files[1]), oldUi);
                byte[] oldHelper = Encoding.UTF8.GetBytes("original-helper");
                if (Files.Length > 3) File.WriteAllBytes(Path.Combine(target, "MouseWithoutBordersHelper.exe"), oldHelper);
                File.WriteAllText(Path.Combine(target, "untouched.config"), "pair-data-not-to-change");
                string backup = CreateBackup(target, Path.Combine(root, "backup"));
                if (Path.GetFileName(backup).Length != 48) throw new Exception("Backup marker length");
                WritePayload(target, payload, manifest);
                // Verify every embedded file exists with its expected bytes;
                // rollback checks alone cannot detect an omitted install file.
                foreach (string name in Files)
                    if (!File.Exists(Path.Combine(target, name)) || HashFile(Path.Combine(target, name)) != manifest[name]) throw new Exception("Payload not installed: " + name);
                if (Files.Length > 3)
                {
                    File.Delete(Path.Combine(target, "MwbNativeDrag.dll"));
                    bool missingRejected = false;
                    try { VerifyInstalledFiles(target, manifest); } catch (InvalidOperationException) { missingRejected = true; }
                    if (!missingRejected) throw new Exception("Missing native runtime accepted");
                    string backupRoot = Path.Combine(root, "backup");
                    File.WriteAllText(Path.Combine(backupRoot, "latest.txt"), Path.GetFileName(backup));
                    if (FindRepairBackup(backupRoot, manifest[Files[0]], new HashSet<string>(new[] { HashBytes(oldExe) })) != backup) throw new Exception("Good recovery backup lost during incomplete-install repair");
                    WritePayload(target, payload, manifest);
                }
                RestoreFiles(backup, target);
                if (HashFile(Path.Combine(target, Files[0])) != HashBytes(oldExe) || HashFile(Path.Combine(target, Files[1])) != HashBytes(oldUi)) throw new Exception("Rollback mismatch");
                if (Files.Length > 3 && (HashFile(Path.Combine(target, "MouseWithoutBordersHelper.exe")) != HashBytes(oldHelper) || File.Exists(Path.Combine(target, "MwbNativeDrag.dll")))) throw new Exception("Native helper rollback mismatch");
                if (File.ReadAllText(Path.Combine(target, "untouched.config")) != "pair-data-not-to-change") throw new Exception("Unrelated config changed");
                bool rejected = false;
                File.AppendAllText(Path.Combine(backup, Files[0]), "corrupt");
                try { VerifyBackup(backup); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("Corrupted backup accepted");
                File.WriteAllText(Path.Combine(root, "result.txt"), "PASS: embedded payload hashes, backup, install writes, rollback, untouched config, corrupt-backup rejection.\r\nNo live service/process/registry operations.\r\n");
                Console.WriteLine("SELF_TEST_PASS " + root);
                return 0;
            }
            catch (Exception ex) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "result.txt"), ex.ToString()); Console.WriteLine(ex); return 1; }
        }
    }
}
