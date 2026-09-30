# 开机动画 1.1.0

**大文件现在真的能装上了。**

1.0.0 的下载是单连接、无续传、无重试：实测在国内下载 51.8 MB 的社区片头会在 26 MB 处断掉，
断了就只能从头再来。1.1.0 把它换成**断点续传 + 自动重试**（HTTP Range，退避 2/4/6/8/10 秒，
最多 6 轮），每轮结束核对字节数，最后仍以 sha256 兜底 —— 下载中断不再等于安装失败。

配套地，社区的投稿上限从 100 MB 提到 **2 GB**（对齐 GitHub Release 的单文件上限），
机器人下载也支持续传；超过 25 MB 的 4K 原画现在有了明确的投稿路径（传到自己的 Release 直链）。

## 这一版改了什么

| | |
|---|---|
| 下载 | 支持 HTTP Range 断点续传；中断后自动退避重试（最多 6 轮）；单次读超时 60 秒，卡住就重试而不是一直等 |
| 界面 | 进度条旁会显示「连接中断，已保留 26.0 MB，4 秒后自动续传（第 3/6 轮）…」，不再是一句干巴巴的失败 |
| 失败 | 屡次中断时会保留已下载的部分，再点一次从断点继续 |
| 安全 | 仍然只接受 https 直链；sha256 + 字节数双重校验，不符就删除、绝不安装 |

已用一个"故意在传输中途掐断连接"的本地服务器实测：前两轮各收到 1 MB 被掐断，
第三轮 Range 续传后拿到完整文件并通过校验（`tools/serve-test.mjs`，见 README）。

## 校验

```
BootAnimation-Setup.exe
  大小   33,987,072 字节 (32.41 MB)
  sha256 7a12c62f67a053804029fa3fece5ea76a16314609116058545db6fb74d173185
```

```
BootAnimation.exe
  大小   33,967,616 字节
  sha256 926ec1c5cb3d283eb20a272f0938e858226af830b43810cefa5e64190edd0700
```

安装方式与 1.0.0 完全相同：免管理员、不改系统引导、不装驱动；没有代码签名，
第一次运行会有 SmartScreen 提示，点「更多信息 → 仍要运行」。

---

# 开机动画 1.0.0

Windows 登录后立刻全屏播放一段片头动画，播完自动消失。

**下载给客户的只有一个文件**：下面的 `BootAnimation-Setup.exe`（32.41 MB，播放器和四段片头都在它里面）。

## 安装

1. 双击 `BootAnimation-Setup.exe`
2. 选一段片头 → 安装 → 完成

不需要管理员权限，不需要安装 .NET 运行时（用每台 Windows 都有的 .NET Framework 4.x）。

### ⚠️ 第一次运行会有 SmartScreen 提示

安装包**目前没有代码签名**，Windows 会显示「Windows 已保护你的电脑」。

点 **更多信息 → 仍要运行** 即可。这是微软对「没有签名证书的新程序」的统一处理，不是检测到了病毒。

想避免这个提示，唯一的正规办法是买代码签名证书（OV 约 $200–400/年），或者上架 Microsoft Store —— 都需要花钱，所以 1.0.0 先这样发。

## 它做什么，不做什么

| | |
|---|---|
| 做 | 登录后全屏播放你选的那段片头，播完自动消失。按 `Esc` 或点一下画面随时退出，5 分钟强制结束。 |
| 不做 | **不改开机画面、不碰系统引导、不改 Winlogon / Shell、不装驱动、不要管理员权限。** |

只动三个地方（卸载时都会清掉，数据目录保留）：

| 位置 | 内容 |
|---|---|
| `%LOCALAPPDATA%\Programs\BootAnimation\` | 程序本体 |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\BootAnimation` | 开机自启（在「设置 → 应用 → 启动」和任务管理器里你自己就能关掉） |
| `%LOCALAPPDATA%\BootAnimation\` | 你的选片、日志、解包缓存（**卸载时保留**，重装后选片还在） |

**为什么不用「改 Shell 启动项让动画出现在桌面之前」那种做法**：那种效果更接近真·开机动画，但包装脚本写错会导致登录后没有桌面，必须进安全模式才能修。本方案最坏情况只是「动画挡了一下」，不会让你进不去系统。

## 内嵌的四段片头

| id | 名称 |
|---|---|
| `brand` | DeepSeek 品牌片头 |
| `cyberpunk` | DeepSeek 赛博朋克片头 |
| `awakening` | DeepSeek 数字角色苏醒 |
| `startup` | DeepSeek 启动问题 |

## 想要更多片头？用社区

[开机动画社区](https://nativedog1.github.io/-Boot-Animation-Community-/) 是一个公开的动画库：浏览、预览、点一下就装进客户端（会先用 sha256 校验再安装）。也可以把你的片子投稿进去。

命令行（软件自带）：

```powershell
# 立刻试看 5 秒
& "$env:LOCALAPPDATA\Programs\BootAnimation\BootAnimation.exe" --play --seconds 5
# 换片头
& "$env:LOCALAPPDATA\Programs\BootAnimation\BootAnimation.exe" --choose
# 播任意视频文件
& "$env:LOCALAPPDATA\Programs\BootAnimation\BootAnimation.exe" --file "D:\我的片头.mp4"
```

## 卸载

**设置 → 应用 → 已安装的应用 → 开机动画 → 卸载**

会删掉程序、开机自启、开始菜单快捷方式和 `bootanim://` 协议注册，**保留你的选片与日志**。

## 校验下载完整性

```
BootAnimation-Setup.exe
  大小   33,983,488 字节 (32.41 MB)
  sha256 b603899f05eb9bd835c856a40f76f040fa9eeaf3af8c89ecfb7cf59c8a521c8a
```

```powershell
Get-FileHash .\BootAnimation-Setup.exe -Algorithm SHA256
```

## 从源码构建

仓库里有完整源码与构建脚本，不需要装任何 SDK：

```powershell
git clone https://github.com/NativeDog1/boot-animation-app.git
cd boot-animation-app
.\build.ps1          # 产出 BootAnimation.exe 与 BootAnimation-Setup.exe
.\test-install.ps1   # 发版前必跑：安装 → 卸载 → 再安装的往返自测
```
