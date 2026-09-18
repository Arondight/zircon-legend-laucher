# Zircon Mir3 Launcher

## 关于本项目

启动器是客户端与游戏服务器之间的入口程序，负责：

- **更新客户端**：连接服务器后与服务器下发的更新清单做 Hash 对比，把不一致的文件下载替换到本地；
- **登录**：向服务器提交账号密码（含设备安全码），完成登录、注册、改密、建角/删角等操作；
- **选角进游戏**：登录后选择角色，携带账号、密码、服务器地址等参数启动游戏客户端（`Legend.exe`），直接进入游戏画面。

配套项目：

- 服务端：[zircon-legend-server](https://github.com/raphaelcheung/zircon-legend-server.git)
- 客户端：[zircon-legend-client](https://github.com/raphaelcheung/zircon-legend-client.git)

## 原项目说明

本仓库 fork 自上游 [zircon-legend-laucher](https://github.com/raphaelcheung/zircon-legend-laucher.git) ，一个基于 WinForms / .NET Framework 4.8 的传奇三启动器。上游项目长期未更新，存在登录被服务器拒绝、更新流程缺陷等问题，故在此基础上做了修复和功能增强。

## 改进清单

1. 修复登录校验：补全设备安全码（CheckSum），解决登录被服务器拒绝的问题；
2. 新增客户端更新源：补丁改由 HTTP(S) 下载，避免占用游戏服务器主线程；
3. 修复重复下载：更新全部完成后才写入更新清单；
4. 修复关闭无响应：完善工作线程异常处理与关闭流程；
5. 修复"保存设置"：配置现可正确写入 `Laucher.ini`；
6. 修复启动游戏停在登录界面：向客户端正确传递服务器地址与会话凭证；
7. 界面优化：新增"更新源"输入框并调整布局。

## 开发环境

- Windows 11
- Visual Studio Community 2026
- .NET Framework 4.8

### 更新源说明

为防止游戏更新占用游戏服务器主线程、影响在线玩家体验，启动器引入客户端更新源机制：更新文件改由 HTTP(S) 直接下发，服务器仅提供更新清单，不抢占服务器主线程资源。

- 留空：走服务器下发，行为与旧版一致；
- 填写：补丁改为从该 HTTP(S) 地址下载，需自行搭建服务；

## 部署注意

- 普通账号登录依赖于服务器配置项；