using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Library;
using Library.Network;
using System.IO.IsolatedStorage;
using System.Security;
using System.Security.Policy;
using System.Security.Permissions;
using Microsoft.Win32;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Reflection;
using C = Library.Network.ClientPackets;
using S = Library.Network.ServerPackets;
using System.Runtime.CompilerServices;
using Library.Network.GeneralPackets;


namespace Launcher
{
    public static class CEnvir
    {
        public delegate void LogEventType(string msg, bool pop, string key);
        public delegate void StatusChangedType();
        public enum MainStepType
        {
            Initializing = 0,
            Ready,
            Connecting,
            Connected,
            Upgrading,
            Upgraded,
            Logining,
            Logon,
            Stopping,
            Stop,
        }

        private struct tagLogItem
        {
            public string Message { get;  set; }
            public string Caption { get; set; }
            public bool NeedPop { get;  set; }
        }

        public static event LogEventType LogEvent;
        public static event StatusChangedType MainStepChanged;

        private static bool FirstAttempted = true;
        public static MainStepType MainStep
        {
            get => _MainStep;
            set
            {
                if (value != _MainStep)
                {
                    _MainStep = value;
                    MainStepChanged?.Invoke();
                }
            }
        }
        private static MainStepType _MainStep = MainStepType.Initializing;
        public static DateTime Now { get; private set; } = DateTime.Now;
        public static DateTime Timeout { get; private set; } = DateTime.MaxValue;

        public static DateTime DisconnectTimtout = DateTime.MaxValue;

        private static bool NeedDisconnect { get; set; } = false;

        private static TcpClient ConnectingClient { get; set; }
        public static CConnection Connection { get; private set; }

        private static ConcurrentQueue<tagLogItem> LogQueue { get; } = new ConcurrentQueue<tagLogItem>();

        private static bool DnsRefreshed { get; set; } = false;
        public static IPAddress IpServer { get; private set; } = null;
        private static ClientUpgradeItem CurrentUpgrade { get; set; } = null;
        private static byte[] CurrentUpgradeDatas { get; set; } = null;
        private static Dictionary<string, ClientUpgradeItem> ClientFileHash { get; } =  new Dictionary<string, ClientUpgradeItem>();
        public static long UpgradeTotalSize { get; private set; } = 0;
        public static long UpgradedSize { get; private set; } = 0;
        //静态补丁源下载状态
        private static ClientUpgradeItem PatchItem = null;
        private static Task PatchTask = null;
        private static bool PatchSucceeded = false;
        private static readonly object PatchLock = new object();

        //private static bool LoadingDb = false;
        public static string RootPath { get; private set; }
        public static string LauncherHash { get; private set; } = "";
        public static string RealIp { get; private set; } = "";
        public static int RealPort { get; private set; } = 0;

        //本机稳定安全码：作为服务器要求的设备验证码上报（登录、建号、改密、建角、删角共用）
        public static string CheckSum { get; } = ComputeCheckSum();
        private static string ComputeCheckSum()
        {
            try
            {
                object machineGuid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null);
                if (machineGuid is string guid && !string.IsNullOrEmpty(guid))
                    return Functions.CalcMD5(guid);
            }
            catch { }

            return Functions.CalcMD5($"{Environment.MachineName}|{Environment.UserName}");
        }

        public static bool DbVersionChecked { get; set; } = false;
        public static bool DbVersionChecking { get; set; } = false;
        private static Queue<ClientUpgradeItem> UpgradeQueue { get; } = new Queue<ClientUpgradeItem>();
        public static List<SelectInfo> SelectCharacters { get; set; }

        [DllImport("dnsapi", EntryPoint = "DnsFlushResolverCache")]
        private static extern int DnsFlushResolverCache();

        public static void Initialize()
        {
            //try
            //{ A(); }
            //catch { }

            Task.Run(() =>
            {
                try
                {
                    LoadClientHash();
                }
                catch (Exception ex)
                {
                    // 清单加载失败不能让启动器一直停在“初始化”，退化为连接后按需计算
                    Log($"加载客户端更新清单失败：{ex.Message}");
                }
                finally
                {
                    MainStep = MainStepType.Ready;
                }
            });

            Task.Run(() =>
            {
                try
                {
                    while (MainStep < MainStepType.Stopping)
                    {
                        try { Process(); }
                        catch (Exception ex)
                        {
                            Log(ex.Message, true, "内部错误");
                            Log(ex.StackTrace);
                        }
                        Thread.Sleep(10);
                    }

                    OnStopping();

                    while (Connection != null && MainStep == MainStepType.Stopping && DateTime.Now < DisconnectTimtout)
                        Thread.Sleep(500);
                }
                finally
                {
                    //无论如何都要置为 Stop，否则主窗体关闭时的自旋等待将永久挂起 UI
                    MainStep = MainStepType.Stop;
                }
            });
        }
        // 本机运行时目录，不参与客户端更新清单
        private static bool IsLocalOnlyDirectory(string name)
        {
            return name.Equals("Errors", StringComparison.OrdinalIgnoreCase)
                || name.Equals("datas", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("_backup", StringComparison.OrdinalIgnoreCase);
        }
        private static void LoadDirHash(DirectoryInfo di, string keyroot)
        {
            foreach (var file in di.GetFiles())
            {
                if (file.Name.Equals("clientupgrade.hash", StringComparison.OrdinalIgnoreCase)) continue;

                string key = Path.Combine(keyroot, file.Name);

                try
                {
                    ClientFileHash[key] = new ClientUpgradeItem()
                    {
                        Key = key,
                        Size = (int)file.Length,
                        Hash = Functions.CalcMD5File(file.FullName),
                    };
                }
                catch (Exception e)
                {
                    // 单个文件读不到（被占用等）不影响整体清单生成
                    Log($"计算文件 Hash 失败：{key} {e.Message}");
                }
            }

            foreach (var dir in di.GetDirectories())
            {
                if (IsLocalOnlyDirectory(dir.Name)) continue;

                LoadDirHash(dir, Path.Combine(keyroot, $"{dir.Name}/"));
            }
        }
        public static void LoadClientHash()
        {
            RootPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string hash_file = Path.Combine(RootPath, "clientupgrade.hash");
            if (File.Exists(hash_file))
            {
                using (StreamReader sr = new StreamReader(hash_file))
                {
                    string line;
                    string[] parts;
                    string[] parts_;
                    while ((line = sr.ReadLine()) != null)
                    {
                        parts = line.Split('=');
                        if (parts.Length < 2)
                        {
                            Log($"读取更新清单发现异常条目：{line}");
                            continue;
                        }

                        parts_ = parts[1].Split(',');
                        if (parts_.Length < 2)
                        {
                            Log($"读取更新清单发现异常条目：{line}");
                            continue;
                        }

                        try
                        {
                            ClientFileHash[parts[0]] = new ClientUpgradeItem()
                            {
                                Key = parts[0],
                                Size = int.Parse(parts_[0]),
                                Hash = parts_[1],
                            };
                        }
                        catch (Exception e)
                        {
                            Log($"读取更新清单发现异常条目：{line}");
                            Log(e.Message);
                            Log(e.StackTrace);
                        }
                    }
                }

                Log($"已读取读取客户端更新清单，共 {ClientFileHash.Count} 个文件");
            }
            else
            {
                // 首次运行（没有清单）时在连接前生成，避免在连接过程中做大量哈希被服务器超时断开。
                // 已排除 Errors/datas/_backup 等本机目录。
                Log($"没有找到更新清单 clientupgrade.hash，正在生成中...");
                DirectoryInfo di = new DirectoryInfo(RootPath);
                LoadDirHash(di, @"./");
                SaveHashFile(hash_file);
                Log($"更新清单保存于 {hash_file}，共 {ClientFileHash.Count} 个文件");
            }
        }
        public static void SaveHashFile(string filename)
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(filename, false))
                {
                    foreach (var item in ClientFileHash)
                    {
                        sw.WriteLine($"{item.Key}={item.Value.Size},{item.Value.Hash}");
                    }
                }
            }
            catch (Exception e)
            {
                // 写缓存失败不应影响连接/更新流程
                Log($"保存更新清单失败：{e.Message}");
            }
        }
        private static void AttemptConnect(IPAddress ip)
        {
            ConnectingClient?.Close();
            ConnectingClient = new TcpClient(ip.AddressFamily);
            ConnectingClient.BeginConnect(ip, RealPort, Connecting, ConnectingClient);
            Timeout = Now.AddSeconds(30);
        }
        private static void ProcDnsConnect()
        {
            if (!DnsRefreshed && Config.NeedFlushDns)
            {
                DnsFlushResolverCache();
                DnsRefreshed = true;
            }

            // 始终使用用户配置的服务器地址重试，禁止回退到硬编码的第三方服务器，
            // 否则域名解析/连接失败时会把账号密码发到别人的服务器上。
            RealIp = Config.IPAddress;
            RealPort = Config.Port;

            if (!FirstAttempted)
            {
#if DEBUG
                CEnvir.Log($"连接失败，使用配置的域名和端口再次尝试连接");
#endif
            }

            try 
            {

                if (IPAddress.TryParse(RealIp, out IPAddress ip))
                    IpServer = ip;
                else
                {
                    var result = Dns.GetHostEntry(Config.IPAddress);
                    foreach (IPAddress ipaddr in result.AddressList)
                    {
                        if (ipaddr.AddressFamily == AddressFamily.InterNetwork
                            || ipaddr.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            IpServer = ipaddr;
                            break;
                        }
                    }
                }

                AttemptConnect(IpServer);
            }
            catch(Exception e)
            {
                if (!FirstAttempted)
                {
                    Log(e.Message, true, "连接");
                    Log(e.StackTrace);
                    MainStep = MainStepType.Ready;
                }

                FirstAttempted = false;
            }
        }
        public static void CheckUpgrade(List<ClientUpgradeItem> server_list)
        {
            if (MainStep != MainStepType.Connected) return;

            string current = $"./{Path.GetFileName(Assembly.GetExecutingAssembly().Location)}";

            foreach(var item in server_list)
            {
                // 按需计算大文件 Hash 可能耗时较长，保持连接不被超时断开
                Connection?.UpdateTimeOut();

                // System.db 由游戏客户端自身的“检查数据更新”流程管理，
                // 启动器不再参与分发，避免两边版本不一致时来回覆盖（每次进游戏都要重下 7MB）。
                if (string.Equals(item.Key, "./Data/System.db", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 启动器自身：无论本地是否已是最新，都要记录服务器期望的 Hash 并传给 Legend.exe。
                // 本地已最新时客户端比对相符不会更新；已过期时由客户端在启动器退出后替换。
                // 不能因为本地 Hash 匹配就提前 continue，否则会把空的 -LauncherHash 传给客户端，
                // 导致客户端报“命令行没有发送启动器的 Hash 码，进行强制更新”。
                if (item.Key == current || item.Key == "./Launcher.exe")
                {
                    if (!string.IsNullOrEmpty(LauncherHash))
                        Log($"启动器没有使用标准名称，当前文件名={current}，期望的文件名 ./Launcher.exe");

                    LauncherHash = item.Hash;
                    continue;
                }

                if (ClientFileHash.TryGetValue(item.Key, out ClientUpgradeItem upgrade) && upgrade.Hash == item.Hash)
                    continue;

                // 本地清单里没有该文件的 Hash 时，只按需计算服务器列出的这个文件，
                // 避免首次运行时把整个客户端目录（含数 GB 资源）全部扫一遍。
                if (TryGetLocalFileHash(item.Key, out string localHash) && localHash == item.Hash)
                {
                    ClientFileHash[item.Key] = new ClientUpgradeItem()
                    {
                        Key = item.Key,
                        Size = item.Size,
                        Hash = item.Hash,
                    };
                    continue;
                }

                UpgradeQueue.Enqueue(item);
                UpgradeTotalSize += item.Size;
            }

            // 记录本次比对结果，下次启动可直接读取清单，无需重新计算
            if (ClientFileHash.Count > 0)
                SaveHashFile(Path.Combine(RootPath, "clientupgrade.hash"));

            if (UpgradeQueue.Count > 0) MainStep = MainStepType.Upgrading;
            else
            {
                Log("客户端已经是最新版本");
                MainStep = MainStepType.Upgraded;
            }
        }

        //按需计算单个本地文件的 Hash，用于和服务器的更新清单比对
        private static bool TryGetLocalFileHash(string key, out string hash)
        {
            hash = null;

            try
            {
                string filename = Path.Combine(RootPath, key);

                if (!File.Exists(filename)) return false;

                hash = Functions.CalcMD5File(filename);
                return true;
            }
            catch (Exception e)
            {
                Log($"计算本地文件 Hash 失败：{key} {e.Message}");
                return false;
            }
        }
        public static void Connected()
        {
            if (MainStep != MainStepType.Connecting) return;

            Log($"成功连接服务器 {Config.IPAddress}:{Config.Port}");
            MainStep = MainStepType.Connected;
        }

        private static void Process()
        {

            Now = DateTime.Now;

            while(LogQueue.TryDequeue(out var log))
            {
                LogEvent?.Invoke(log.Message, log.NeedPop, log.Caption);
            }


            if (MainStep < MainStepType.Ready) return;

            Connection?.Process();


            if (MainStep == MainStepType.Connecting && !(Connection?.ServerConnected ?? false))
            {
                if (ConnectingClient == null && Connection == null)
                    ProcDnsConnect();
                else if (Now > Timeout)
                {
                    ConnectingClient?.Close();
                    ConnectingClient = null;
                    Log("服务器连接超时", true, "连接");
                    MainStep = MainStepType.Ready;
                }

                return;
            }

            if (MainStep >= MainStepType.Connected
                && MainStep < MainStepType.Stopping 
                && !(Connection?.ServerConnected ?? false))
            {

                return;
            }

            if (NeedDisconnect)
            {
                Connection?.TryDisconnect();
                return;
            }

            if (MainStep == MainStepType.Upgrading)
            {
                //处理补丁源下载结果：仅在 PatchTask 运行期间阻塞出队，避免并发多个下载并把补丁任务引用互相覆盖
                if (PatchItem != null && PatchTask != null)
                {
                    if (PatchTask.IsCompleted)
                    {
                        bool ok;
                        ClientUpgradeItem finished;
                        lock (PatchLock)
                        {
                            ok = PatchSucceeded;
                            finished = PatchItem;
                            PatchItem = null;
                            PatchTask = null;
                        }

                        if (!ok)
                        {
                            //补丁源获取失败则退回游戏服务器下发，保证可用
                            Log($"补丁服务器获取 {finished.Key} 失败，改用服务器下发", false, "客户端更新");
                            CurrentUpgrade = finished;
                            CurrentUpgradeDatas = null;

                            Connection.Enqueue(new C.UpgradeClient()
                            {
                                FileKey = finished.Key,
                            });
                        }
                    }

                    return;
                }

                //游戏服务器下发改走 Upgrade() 流式接收，期间不处理补丁队列
                if (CurrentUpgrade != null)
                    return;

                if (UpgradeQueue.Count <= 0)
                {
                    //所有文件下载/下发完成后才写 hash，避免旧 hash 导致下次重复下载
                    SaveHashFile(Path.Combine(RootPath, "clientupgrade.hash"));
                    Log($"客户端完成更新，本次更新了 {Functions.BytesToString(UpgradeTotalSize)} 数据");
                    MainStep = MainStepType.Upgraded;
                    return;
                }

                var item = UpgradeQueue.Dequeue();

                if (item != null)
                {
                    if (!string.IsNullOrEmpty(Config.ClientUrl))
                    {
                        //走静态补丁源拉取，避免经游戏服务器主线程下发
                        Log($"正在下载 {item.Key} ...", false, "客户端更新");
                        lock (PatchLock)
                        {
                            PatchItem = item;
                            PatchSucceeded = false;
                            PatchTask = Task.Run(() => DownloadViaHttp(item));
                        }
                    }
                    else
                    {
                        Log($"正在更新 {item.Key} ...", false, "客户端更新");
                        CurrentUpgrade = item;
                        CurrentUpgradeDatas = null;

                        Connection.Enqueue(new C.UpgradeClient()
                        {
                            FileKey = item.Key,
                        });
                    }
                }

                return;
            }

        }

        public static void Upgrade(string file, int total_size, int index, byte[] datas)
        {
            if (MainStep != MainStepType.Upgrading || CurrentUpgrade == null || file != CurrentUpgrade.Key) return;

            if (total_size <= 0)
            {
                Log($"更新 {CurrentUpgrade.Key} 时收到 0 大小的异常数据包，更新失败", true, "客户端更新");
                CurrentUpgrade = null;
                CurrentUpgradeDatas = null;
                NeedDisconnect = true;

                return;
            }

            if (CurrentUpgradeDatas == null)
                CurrentUpgradeDatas = new byte[total_size];

            try
            {
                datas.CopyTo(CurrentUpgradeDatas, index);

                UpgradedSize += datas.Length;

                if ((index + datas.Length) >= total_size)
                {
                    string filename = Path.Combine(RootPath, CurrentUpgrade.Key);

                    string path = Path.GetDirectoryName(filename);

                    if (!Directory.Exists(path)) 
                        Directory.CreateDirectory(path);

                    File.WriteAllBytes(filename, CurrentUpgradeDatas);

                    Log($"更新成功 {CurrentUpgrade.Key}，文件大小 {Functions.BytesToString(CurrentUpgradeDatas.Length)}");

                    if (ClientFileHash.TryGetValue(CurrentUpgrade.Key, out ClientUpgradeItem item))
                    {
                        item.Size = CurrentUpgrade.Size;
                        item.Hash = CurrentUpgrade.Hash;
                    }
                    else
                        ClientFileHash.Add(CurrentUpgrade.Key, new ClientUpgradeItem()
                        {
                            Hash = CurrentUpgrade.Hash,
                            Size = CurrentUpgrade.Size,
                            Key = CurrentUpgrade.Key,
                        });

                    CurrentUpgrade = null;
                    CurrentUpgradeDatas = null;
                }
            }
            catch (Exception ex)
            {
                Log($"更新文件 {CurrentUpgrade.Key} 时发生异常，即将断开连接", true, "客户端更新");
                Log(ex.Message);
                Log(ex.StackTrace);
                CurrentUpgrade = null;
                CurrentUpgradeDatas = null;
                NeedDisconnect = true;
            }

            Thread.Sleep(1);
        }

        //从静态补丁源(HTTP)下载一个文件并校验回写清单；失败时仅置标志，由主循环退回服务器下发
        private static void DownloadViaHttp(ClientUpgradeItem item)
        {
            try
            {
                string relative = item.Key.Replace('\\', '/').TrimStart('.', '/');
                string url = $"{Config.ClientUrl.TrimEnd('/')}/{relative}";

                byte[] data;
                using (TimedWebClient client = new TimedWebClient())
                {
                    client.Timeout = Math.Max(30000, (int)Config.TimeOutDuration.TotalMilliseconds * 4);
                    client.Headers[HttpRequestHeader.UserAgent] = "ZirconLauncher/1.0";
                    data = client.DownloadData(url);
                }

                if (data == null || Functions.CalcMD5(data) != item.Hash)
                {
                    Log($"补丁文件 {item.Key} 校验失败，准备回退到服务器下发", false, "客户端更新");
                    lock (PatchLock) PatchSucceeded = false;
                    return;
                }

                string filename = Path.Combine(RootPath, item.Key);
                string path = Path.GetDirectoryName(filename);
                if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                    Directory.CreateDirectory(path);

                string tmp = filename + ".tmp";
                File.WriteAllBytes(tmp, data);

                lock (PatchLock)
                {
                    if (File.Exists(filename)) File.Delete(filename);
                    File.Move(tmp, filename);

                    if (ClientFileHash.TryGetValue(item.Key, out ClientUpgradeItem exist))
                    {
                        exist.Hash = item.Hash;
                        exist.Size = item.Size;
                    }
                    else
                        ClientFileHash.Add(item.Key, new ClientUpgradeItem()
                        {
                            Hash = item.Hash,
                            Size = item.Size,
                            Key = item.Key,
                        });

                    UpgradedSize += item.Size;
                    PatchSucceeded = true;
                }

                Log($"更新成功 {item.Key}，文件大小 {Functions.BytesToString(data.Length)}");
            }
            catch (Exception ex)
            {
                Log($"补丁服务器获取 {item.Key} 出错：{ex.Message}", false, "客户端更新");
                lock (PatchLock) PatchSucceeded = false;
            }
        }

        //WebClient 默认无请求超时，派生类补上，避免补丁源挂起导致卡在"获取中"
        private sealed class TimedWebClient : WebClient
        {
            public int Timeout { get; set; } = 60000;
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null) request.Timeout = Timeout;
                return request;
            }
        }

        public static void Connect()
        {
            if (MainStep != MainStepType.Ready) return;

            // 每次新的连接都重置重试标记，避免沿用上一次失败的“已重试过”状态
            FirstAttempted = true;
            MainStep = MainStepType.Connecting;
        }
        public static void Login(string password)
        {
            if (MainStep != MainStepType.Upgraded) return;

            Connection.Enqueue(new C.LoginSimple()
            {
                CheckSum = CheckSum,
                Password = password,
                EMailAddress = Config.Account,
            });

            MainStep = MainStepType.Logining;
        }
        public static void Stop()
        {
            if (MainStep >= MainStepType.Stopping) return;

            MainStep = MainStepType.Stopping;
        }
        public static void Disconnect()
        {
            NeedDisconnect = false;
            Connection = null;
            lock (PatchLock)
            {
                PatchItem = null;
                PatchTask = null;
                PatchSucceeded = false;
            }
            UpgradeQueue.Clear();
            CurrentUpgrade = null;
            CurrentUpgradeDatas = null;
            ConnectingClient = null;
            // 不要清空 ClientFileHash：它只在 Initialize 时加载一次，
            // 清空后重新连接会把服务器上所有文件都当成“已变更”，导致整包重新下载。
            UpgradedSize = 0;
            UpgradeTotalSize = 0;

            MainStep = MainStep == MainStepType.Stopping ? MainStepType.Stop : MainStepType.Ready;
        }
        private static void OnStopping()
        {
            if (Connection == null) return;

            Connection.TrySendDisconnect(new Disconnect() { Reason = DisconnectReason.Unknown });
            DisconnectTimtout = DateTime.Now.AddSeconds(1);
            //Connection?.TryDisconnect();
            //Disconnect();
        }
        private static void Connecting(IAsyncResult result)
        {
            try
            {
                TcpClient client = (TcpClient)result.AsyncState;
                client.EndConnect(result);

                if (!client.Connected) return;

                if (client != ConnectingClient)
                {
                    ConnectingClient = null;
                    client.Close();
                    return;
                }

                //ConnectionTime = Now.AddSeconds(5); //Add 5 more seconds to timeout for delayed HandShake
                Connection = new CConnection(client);
                ConnectingClient = null;
            }
            catch(Exception ex)
            {
                Log(ex.Message);
                Log(ex.StackTrace);
            }
        }

        public static void ResponseLogin(S.LoginSimple p)
        {
            if (MainStep != MainStepType.Logining) return;

            switch (p.Result)
            {
                case LoginResult.Disabled:
                    Log("当前禁止登录.", true, "登录");
                    break;
                case LoginResult.BadEMail:
                    Log("账号不符合规范.", true, "登录");
                    break;
                case LoginResult.BadPassword:
                    Log("密码不符合规范.", true, "登录");
                    break;
                case LoginResult.AccountNotExists:
                    Log("账号不存在.", true, "登录");
                    break;
                case LoginResult.AccountNotActivated:
                    Log("账号没有激活.", true, "登录");

                    break;
                case LoginResult.WrongPassword:
                    Log("密码错误.", true, "登录");
                    break;
                case LoginResult.Banned:
                    DateTime expiry = CEnvir.Now.Add(p.Duration);

                    Log($"该账号已被禁用.\n\n" +
                        $"原因: {p.Message}\n" +
                        $"解禁时间: {expiry}\n" +
                        $"距离解禁还有: {Math.Floor(p.Duration.TotalHours):#,##0} 小时, {p.Duration.Minutes} 分, {p.Duration.Seconds} 秒", true, "登录");

                    break;
                case LoginResult.AlreadyLoggedIn:
                    Log("该账号正在使用中，稍候再试.", true, "登录");
                    break;
                case LoginResult.AlreadyLoggedInPassword:
                    Log("该账号正在使用中\n" +
                        "新密码已发到 E-Mail 邮箱...", true, "登录");
                    break;
                case LoginResult.AlreadyLoggedInAdmin:
                    Log("账号正在被管理员接管", true, "登录");
                    break;
                case LoginResult.Success:
                    p.Characters.Sort((x1, x2) => x2.LastLogin.CompareTo(x1.LastLogin));

                    SelectCharacters = p.Characters;

                    if (!string.IsNullOrEmpty(p.Message)) Log($"登录成功 {p.Message}");
                    else Log("登录成功!");

                    MainStep = MainStepType.Logon;
                    return;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            MainStep = MainStepType.Upgraded;
        }
        public static void CreateAccount(string account, string password)
        {
            if (MainStep != MainStepType.Upgraded) return;

            Connection.Enqueue(new C.NewAccount()
            {
                BirthDate = Now,
                EMailAddress = account,
                Password = password,
                RealName = "",
                CheckSum = CheckSum,
                Referral = "",
            });
        }
        public static void ChangePassword(string account, string original_password, string new_password)
        {
            if (MainStep != MainStepType.Upgraded) return;

            Connection.Enqueue(new C.ChangePassword()
            {
                CurrentPassword = original_password,
                EMailAddress = account,
                NewPassword = new_password,
                CheckSum = CheckSum
            });
        }

        public static void ResponseChangePassword(S.ChangePassword p)
        {
            switch (p.Result)
            {
                case ChangePasswordResult.Disabled:
                    Log("修改密码被禁用.", true, "修改密码");
                    break;
                case ChangePasswordResult.BadEMail:
                    Log("E-Mail 不符合规范.", true, "修改密码");
                    break;
                case ChangePasswordResult.BadCurrentPassword:
                    Log("当前密码不符合规范.", true, "修改密码");
                    break;
                case ChangePasswordResult.BadNewPassword:
                    Log("新密码不符合规范.", true, "修改密码");
                    break;
                case ChangePasswordResult.AccountNotFound:
                    Log("账号不存在.", true, "修改密码");
                    break;
                case ChangePasswordResult.AccountNotActivated:
                    Log("账号未激活.", true, "修改密码");
                    break;
                case ChangePasswordResult.WrongPassword:
                    Log("密码错误.", true, "修改密码");
                    break;
                case ChangePasswordResult.Banned:
                    DateTime expiry = CEnvir.Now.Add(p.Duration);
                    Log($"该账号已被禁用: {p.Message}\n" +
                                                         $"解禁时间: {expiry}\n" +
                                                         $"距离解封还有: {Math.Floor(p.Duration.TotalHours):#,##0} 小时, {p.Duration.Minutes} 分钟, {p.Duration.Seconds} 秒", true, "修改密码");

                    break;
                case ChangePasswordResult.Success:
                    Log("密码修改成功.", false, "修改密码");
                    break;
            }
        }
        public static void ResponseCreateAccount(S.NewAccount p)
        {

            switch (p.Result)
            {
                case NewAccountResult.Disabled:
                    Log("创建账号的功能被禁用.", true, "创建账号");
                    break;
                case NewAccountResult.BadEMail:
                    Log("E-Mail 地址不符合规范.", true, "创建账号");
                    break;
                case NewAccountResult.BadPassword:
                    Log("密码不符合规范.",true, "创建账号");
                    break;
                case NewAccountResult.BadRealName:
                    Log("真实名称不符合规范.", true, "创建账号");
                    break;
                case NewAccountResult.AlreadyExists:
                    Log("E-Mail 地址已被使用.", true, "创建账号");
                    break;
                case NewAccountResult.BadReferral:
                    Log("推荐人的 E-Mail 地址不符合规范.", true, "创建账号");
                    break;
                case NewAccountResult.ReferralNotFound:
                    Log("找不到推荐人的 E-Mail 地址.", true, "创建账号");
                    break;
                case NewAccountResult.ReferralNotActivated:
                    Log("推荐人的 E-Mail 地址没有激活.", true, "创建账号");
                    break;
                case NewAccountResult.Success:
                    Log("你的账号创建成功，祝你游戏愉快.", false, "创建账号");
                    break;
            }
        }

        public static void CreateCharacter(string name, MirGender gender, MirClass cls)
        {
            if (MainStep != MainStepType.Logon) return;

            Connection.Enqueue(new C.NewCharacter
            {
                CharacterName = name,
                Class = cls,
                Gender = gender,
                HairType = 1,
                HairColour = Color.FromArgb(255, 0, 0, 0),
                ArmourColour = cls == MirClass.Assassin ? Color.FromArgb(0) : Color.FromArgb(255, 0, 0, 0),
                CheckSum = CheckSum,
            });
        }
        public static void ResponseCreateCharacter(S.NewCharacter p)
        {
            switch (p.Result)
            {
                case NewCharacterResult.Disabled:
                    Log("创建角色功能被禁用.", true, "创建角色");
                    break;
                case NewCharacterResult.BadCharacterName:
                    Log("角色名称不符合规范.", true, "创建角色");
                    break;
                case NewCharacterResult.BadHairType:
                    Log("错误: 无效的发型.", true, "创建角色");
                    break;
                case NewCharacterResult.BadHairColour:
                    Log("错误: 无效的头发颜色.", true, "创建角色");
                    break;
                case NewCharacterResult.BadArmourColour:
                    Log("错误: 无效的盔甲颜色.", true, "创建角色");
                    break;
                case NewCharacterResult.BadGender:
                    Log("错误: 无效的性别.", true, "创建角色");
                    break;
                case NewCharacterResult.BadClass:
                    Log("错误: 无效的职业.", true, "创建角色");
                    break;  
                case NewCharacterResult.ClassDisabled:
                    Log("选中的职业当前不可用.", true, "创建角色");
                    break;
                case NewCharacterResult.MaxCharacters:
                    Log("可创建的角色数量已达上限.", true, "创建角色");
                    break;
                case NewCharacterResult.AlreadyExists:
                    Log("角色已存在.", true, "创建角色");
                    break;
                case NewCharacterResult.Success:

                    SelectCharacters.Add(p.Character);
                    Log("角色创建成功.", false, "创建角色");
                    break;
            }
        }

        public static void DeleteCharacter(int index)
        {
            if (MainStep != MainStepType.Logon) return;

            Connection.Enqueue(new C.DeleteCharacter()
            {
                CharacterIndex = index,
                CheckSum = CheckSum
            });
        }
        public static void ResponseDeleteCharacter(S.DeleteCharacter p)
        {
            switch (p.Result)
            {
                case DeleteCharacterResult.Disabled:
                    Log("删除角色被禁用.", true, "删除角色");
                    break;
                case DeleteCharacterResult.AlreadyDeleted:
                    Log("该角色已经被删除了.", true, "删除角色");
                    break;
                case DeleteCharacterResult.NotFound:
                    Log("角色没找到.", true, "删除角色");
                    break;
                case DeleteCharacterResult.Success:
                    
                    for(int i = 0; i < SelectCharacters.Count; i ++)
                    {
                        if (SelectCharacters[i].CharacterIndex == p.DeletedIndex)
                        {
                            SelectCharacters.RemoveAt(i);
                            break;
                        }
                    }

                    Log("角色删除成功.", false, "删除角色");
                    break;
            }
        }
        public static void Log(string message, bool pop = false, string caption = null)
        {
            if (string.IsNullOrEmpty(message)) return;

            LogQueue.Enqueue(new tagLogItem()
            {
                Caption = caption,
                Message = message,
                NeedPop = pop
            });
        }
    }
}
