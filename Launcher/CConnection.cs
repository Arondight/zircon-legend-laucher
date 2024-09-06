using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows.Forms;
using Library.Network;
using Library;
using G = Library.Network.GeneralPackets;
using S = Library.Network.ServerPackets;
using C = Library.Network.ClientPackets;

namespace Launcher
{
    public sealed class CConnection : BaseConnection
    {
        protected override TimeSpan TimeOutDelay => Config.TimeOutDuration;

        public bool ServerConnected { get; set; }

        public int Ping { get; set; }

        private readonly List<byte> DbUpgrade = new List<byte>();

        public CConnection(TcpClient client)
            : base(client)
        {
            OnException += (o, e) => CEnvir.Log(e.ToString());

            UpdateTimeOut();

            AdditionalLogging = true;

            BeginReceive();
        }

        public override void TryDisconnect()
        {
            Disconnect();
        }
        public override void Disconnect()
        {
            base.Disconnect();

            if (CEnvir.Connection == this)
            {
                CEnvir.Disconnect();
            }
        }
        public override void TrySendDisconnect(Packet p)
        {
            SendDisconnect(p);
        }

        public void Process(G.Disconnect p)
        {
            Disconnecting = true;


            switch (p.Reason)
            {
                case DisconnectReason.Unknown:
                    CEnvir.Log("服务器断开连接，原因: 未知");
                    break;
                case DisconnectReason.TimedOut:
                    CEnvir.Log("服务器断开连接，原因: 连接超时.");
                    break;
                case DisconnectReason.ServerClosing:
                    CEnvir.Log("服务器断开连接，原因: 服务器关闭.");
                    break;
                case DisconnectReason.AnotherUser:
                    CEnvir.Log("服务器断开连接，原因: 其他用户使用你的账号登录.");
                    break;
                case DisconnectReason.AnotherUserAdmin:
                    CEnvir.Log("服务器断开连接，原因: 管理员接管了你的账号.");
                    break;
                case DisconnectReason.Banned:
                    CEnvir.Log("服务器断开连接，原因: 你的账号被禁用.");
                    break;
                case DisconnectReason.Crashed:
                    CEnvir.Log("服务器断开连接，原因: 服务器崩溃.");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            if (this == CEnvir.Connection)
                CEnvir.Disconnect();

        }
        public void Process(G.Connected p)
        {
            Enqueue(new G.Connected());
            ServerConnected = true;
            CEnvir.Connected();
        }
        public void Process(S.CheckClientHash p)
        {
            CEnvir.CheckUpgrade(p.ClientFileHash);
        }
        public void Process(G.Ping p)
        {
            Enqueue(new G.Ping());
        }
        public void Process(G.PingResponse p)
        {
            Ping = p.Ping;
        }
        public void Process(G.GoodVersion p)
        {
            Enqueue(new C.SelectLanguage { Language = "Chinese" });
        }

        public void Process(S.UpgradeClient p)
        {
            CEnvir.Upgrade(p.FileKey, p.TotalSize, p.StartIndex, p.Datas);
        }

        public void Process(S.SelectLogout p)
        { }

        public void Process(S.LoginSimple p)
        {
            CEnvir.ResponseLogin(p);
        }

        public void Process(S.NewAccount p)
        {
            CEnvir.ResponseCreateAccount(p);
        }

        public void Process(S.ChangePassword p)
        {
            CEnvir.ResponseChangePassword(p);
        }

        public void Process(S.NewCharacter p)
        {
            CEnvir.ResponseCreateCharacter(p);
        }


        public void Process(S.DeleteCharacter p)
        {
            CEnvir.ResponseDeleteCharacter(p);
        }

        //public void Process(S.SelectLogout p)
        //{
        //    CEnvir.ReturnToLogin();
        //    ((LoginScene)DXControl.ActiveScene).LoginBox.Visible = true;
        //}
        //public void Process(S.GameLogout p)
        //{
        //    DXSoundManager.StopAllSounds();

        //    GameScene.Game.Dispose();

        //    DXSoundManager.Play(SoundIndex.SelectScene);

        //    SelectScene scene;

        //    p.Characters.Sort((x1, x2) => x2.LastLogin.CompareTo(x1.LastLogin));

        //    DXControl.ActiveScene = scene = new SelectScene(Config.IntroSceneSize)
        //    {
        //        SelectBox = { CharacterList = p.Characters },
        //    };

        //    CEnvir.Storage = CEnvir.MainStorage;

        //    scene.SelectBox.UpdateCharacters();
        //}
        //public void Process(S.UpgradeClient p)
        //{
        //    CEnvir.Upgrade(p.FileKey, p.TotalSize, p.StartIndex, p.Datas);
        //}


        //public void Process(S.StartGame p)
        //{
        //    try
        //    {

        //    SelectScene select = DXControl.ActiveScene as SelectScene;
        //    if (select == null) return;

        //    select.SelectBox.StartGameAttempted = false;


        //    DXMessageBox box;
        //    DateTime expiry;
        //    switch (p.Result)
        //    {
        //        case StartGameResult.Disabled:
        //            DXMessageBox.Show("开始游戏功能被禁用.", "开始游戏");
        //            break;
        //        case StartGameResult.Deleted:
        //            DXMessageBox.Show("你不能用已删除的角色进入游戏.", "开始游戏");
        //            break;
        //        case StartGameResult.Delayed:
        //            expiry = CEnvir.Now.Add(p.Duration);

        //            box = DXMessageBox.Show($"该角色刚刚下线，稍候才能上线.\n" +
        //                                    $"等待: {Math.Floor(p.Duration.TotalHours):#,##0} 小时, {p.Duration.Minutes} 分, {p.Duration.Seconds} 秒", "开始游戏");

        //            box.ProcessAction = () =>
        //            {
        //                if (CEnvir.Now > expiry)
        //                {
        //                    if (select.SelectBox.CanStartGame)
        //                        select.SelectBox.StartGame();
        //                    box.ProcessAction = null;
        //                    return;
        //                }

        //                TimeSpan remaining = expiry - CEnvir.Now;

        //                box.Label.Text = $"该角色刚刚下线，稍候才能上线.\n" +
        //                                 $"等待: {Math.Floor(remaining.TotalHours):#,##0} 小时, {remaining.Minutes:#,##0} 分, {remaining.Seconds} 秒";
        //            };
        //            break;
        //        case StartGameResult.UnableToSpawn:
        //            DXMessageBox.Show("无法进入游戏，生成角色失败.", "开始游戏");
        //            break;
        //        case StartGameResult.NotFound:
        //            DXMessageBox.Show("无法进入游戏，该角色没找到.", "开始游戏");
        //            break;
        //        case StartGameResult.Success:
        //            select.Dispose();
        //            DXSoundManager.StopAllSounds();

        //            GameScene scene = new GameScene(Config.GameSize);
        //            DXControl.ActiveScene = scene;

        //            scene.MapControl.MapInfo = Globals.MapInfoList.Binding.FirstOrDefault(x => x.Index == p.StartInformation.MapIndex);
        //            GameScene.Game.QuestLog = p.StartInformation.Quests;

        //            GameScene.Game.NPCAdoptCompanionBox.AvailableCompanions = p.StartInformation.AvailableCompanions;
        //            GameScene.Game.NPCAdoptCompanionBox.RefreshUnlockButton();

        //            GameScene.Game.NPCCompanionStorageBox.Companions = p.StartInformation.Companions;
        //            GameScene.Game.NPCCompanionStorageBox.UpdateScrollBar();

        //            GameScene.Game.Companion = GameScene.Game.NPCCompanionStorageBox.Companions.FirstOrDefault(x => x.Index == p.StartInformation.Companion);

        //            scene.User = new UserObject(p.StartInformation);

        //            GameScene.Game.BuffBox.BuffsChanged();
        //            GameScene.Game.RankingBox.Observable = p.StartInformation.Observable;

        //            GameScene.Game.StorageSize = p.StartInformation.StorageSize;

        //            if (!string.IsNullOrEmpty(p.Message)) DXMessageBox.Show(p.Message, "开始游戏");


        //            break;
        //        }
        //    }
        //    catch (Exception e)
        //    {
        //        Console.WriteLine(e);
        //        throw;
        //    }
        //}

        //public void Process(S.CheckClientDb p)
        //{
        //    if (!CEnvir.DbVersionChecking) return;

        //    if (p.IsUpgrading)
        //    {
        //        if (p.CurrentIndex == 0)
        //            DbUpgrade.Clear();

        //        DbUpgrade.AddRange(p.Datas);

        //        if (p.CurrentIndex >= (p.TotalCount - 1))
        //        {

        //            byte[] bytes = DbUpgrade.ToArray();
        //            DbUpgrade.Clear();

        //            File.WriteAllBytes(@"./Data/System.db", bytes);
        //            CEnvir.DbVersionChecking = false;
        //            CEnvir.DbVersionChecked = true;
        //        }
        //    }
        //    else
        //    {
        //        DbUpgrade.Clear();
        //        CEnvir.DbVersionChecking = false;
        //        CEnvir.DbVersionChecked = true;
        //    }
        //}

    }
}

