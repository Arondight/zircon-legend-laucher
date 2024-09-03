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
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Reflection;
using C = Library.Network.ClientPackets;
using S = Library.Network.ServerPackets;
using System.Runtime.CompilerServices;


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

        private static TcpClient ConnectingClient { get; set; }
        public static CConnection Connection { get; private set; }

        private static ConcurrentQueue<tagLogItem> LogQueue { get; } = new ConcurrentQueue<tagLogItem>();

        private static bool DnsRefreshed { get; set; } = false;
        private static IPAddress IpServer { get; set; } = null;
        private static ClientUpgradeItem CurrentUpgrade { get; set; } = null;
        private static byte[] CurrentUpgradeDatas { get; set; } = null;
        private static Dictionary<string, ClientUpgradeItem> ClientFileHash { get; } =  new Dictionary<string, ClientUpgradeItem>();
        public static long UpgradeTotalSize { get; private set; } = 0;
        public static long UpgradedSize { get; private set; } = 0;

        private static bool LoadingDb = false;
        public static string RootPath { get; private set; }

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
                LoadClientHash();
                MainStep = MainStepType.Ready;
            });

            Task.Run(() =>
            {


                while (MainStep < MainStepType.Stopping)
                {
                    Process();
                    Thread.Sleep(10);
                }

                OnStopping();
                MainStep = MainStepType.Stop;
            });
        }
        private static void LoadDirHash(DirectoryInfo di, string keyroot)
        {
            FileInfo[] files = di.GetFiles();
            byte[] datas;
            string key;
            foreach (var file in files)
            {
                key = Path.Combine(keyroot, file.Name);
                datas = File.ReadAllBytes(file.FullName);
                ClientFileHash[key] = new ClientUpgradeItem()
                {
                    Key = key,
                    Size = datas.Length,
                    Hash = Functions.CalcMD5(datas),
                };
                //Log($"已计算文件：{file.Name}");
            }

            DirectoryInfo[] directories = di.GetDirectories();
            foreach (var dir in directories)
            {
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
                Log($"没有找到更新清单 clientupgrade.hash，正在重新生成中...");
                DirectoryInfo di = new DirectoryInfo(RootPath);
                LoadDirHash(di, @"./");
                SaveHashFile(hash_file);
                Log($"更新清单保存于 {hash_file}，共 {ClientFileHash.Count} 个文件");
            }
        }
        public static void SaveHashFile(string filename)
        {
            using (StreamWriter sw = new StreamWriter(filename, false))
            {
                foreach (var item in ClientFileHash)
                {
                    sw.WriteLine($"{item.Key}={item.Value.Size},{item.Value.Hash}");
                }
            }
        }
        private static void AttemptConnect(IPAddress ip)
        {
            ConnectingClient?.Close();
            ConnectingClient = new TcpClient(ip.AddressFamily);
            ConnectingClient.BeginConnect(ip, Config.Port, Connecting, ConnectingClient);
            Timeout = Now.AddSeconds(30);
        }
        private static void ProcDnsConnect()
        {
            if (!DnsRefreshed)
            {
                DnsFlushResolverCache();
                DnsRefreshed = true;
            }


            try 
            {
                var result = Dns.GetHostEntry(Config.IPAddress);
                foreach (var ip in result.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork
                        || ip.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        IpServer = ip;
                        break;
                    }
                }

                AttemptConnect(IpServer);
            }
            catch(Exception e)
            {
                Log(e.Message, true, "连接");
                Log(e.StackTrace);
                MainStep = MainStepType.Ready;
            }
        }
        public static void CheckUpgrade(List<ClientUpgradeItem> server_list)
        {
            if (MainStep != MainStepType.Connected) return;

            string current = $"./{Path.GetFileName(Assembly.GetExecutingAssembly().Location)}";

            foreach(var item in server_list)
            {
                if (ClientFileHash.TryGetValue(item.Key, out ClientUpgradeItem upgrade) && upgrade.Hash == item.Hash)
                    continue;

                if (item.Key == current) continue;

                UpgradeQueue.Enqueue(item);
                UpgradeTotalSize += item.Size;
            }

            if (UpgradeQueue.Count > 0) MainStep = MainStepType.Upgrading;
            else
            {
                Log("客户端已经是最新版本");
                MainStep = MainStepType.Upgraded;
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

            if (MainStep == MainStepType.Upgrading)
            {
                if (CurrentUpgrade == null)
                {
                    if (UpgradeQueue.Count <= 0)
                    {
                        SaveHashFile(Path.Combine(RootPath, "clientupgrade.hash"));
                        Log($"客户端完成更新，本次更新了 {Functions.BytesToString(UpgradeTotalSize)} 数据");
                        MainStep = MainStepType.Upgraded;
                        return;
                    }

                    var item = UpgradeQueue.Dequeue();

                    if (item != null)
                    {
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
            if (file != CurrentUpgrade.Key) return;

            if (total_size <= 0)
            {
                Log($"更新 {CurrentUpgrade.Key} 时收到 0 大小的异常数据包，更新失败", true, "客户端更新");
                CurrentUpgrade = null;
                CurrentUpgradeDatas = null;
                Connection.TryDisconnect();

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
                    string path = Path.Combine(RootPath, CurrentUpgrade.Key);
                    File.WriteAllBytes(path, CurrentUpgradeDatas);

                    Log($"更新成功 {path}，文件大小 {Functions.BytesToString(CurrentUpgradeDatas.Length)}");

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
                Log($"更新异常 {CurrentUpgrade.Key}", true, "客户端更新");
                Log(ex.Message);
                Log(ex.StackTrace);
                CurrentUpgrade = null;
                CurrentUpgradeDatas = null;
                Connection.TryDisconnect();
            }
        }

        public static void Connect()
        {
            if (MainStep != MainStepType.Ready) return;

            MainStep = MainStepType.Connecting;
        }
        public static void Login(string password)
        {
            if (MainStep != MainStepType.Upgraded) return;

            Connection.Enqueue(new C.LoginSimple()
            {
                CheckSum = "",
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
            Connection = null;
            UpgradeQueue.Clear();
            CurrentUpgrade = null;
            CurrentUpgradeDatas = null;
            ConnectingClient = null;
            ClientFileHash.Clear();

            UpgradedSize = 0;
            UpgradeTotalSize = 0;

            string hash_file = Path.Combine(RootPath, @"./clientupgrade.hash");

            MainStep = MainStepType.Ready;
        }
        private static void OnStopping()
        {
            Connection?.TryDisconnect();
            Disconnect();
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
                CheckSum = "",
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
                CheckSum = ""
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
                CheckSum = "",
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
                CheckSum = "",
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
