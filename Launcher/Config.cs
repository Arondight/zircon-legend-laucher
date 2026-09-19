using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Library;

namespace Launcher
{
    [ConfigPath(@".\Laucher.ini")]
    public static class Config
    {
        [ConfigSection("Network")]
        public static string IPAddress { get; set; } = "localhost";
        public static int Port { get; set; } = 7000;
        public static TimeSpan TimeOutDuration { get; set; } = TimeSpan.FromSeconds(15);
        public static bool NeedFlushDns { get; set; } = false;
        //客户端更新源地址(可空)。配置后更新文件改走 HTTP 拉取，避免占用游戏服务器主线程
        public static string ClientUrl { get; set; } = string.Empty;


        [ConfigSection("Graphics")]
        public static bool FullScreen { get; set; } = false;
        public static Size GameSize { get; set; } = new Size(1600, 900);

        [ConfigSection("Login")]
        public static bool Remember { get; set; } = false;
        public static string Account { get; set; } = string.Empty;
        public static string Password { get; set; } = string.Empty;
    }
}
