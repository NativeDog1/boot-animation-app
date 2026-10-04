# 开机动画 1.2.0

**「登录之后要过一会儿才播」和「动画不是居中的、缺少了一部分」—— 两条反馈都是真因，都修了。**

## 1. 延迟：不是"加载慢"，是故意睡了 3 秒

自启命令写的是 `--play --delay 3`，代码里就是 `Thread.Sleep(delay * 1000)`。
这个参数的本意是"等桌面铺好再抢屏幕"，但它**叠加**在 `HKCU\...\Run` 自身的延迟之上：
Run 键的进程本来就要等登录之后若干秒才被拉起，再睡 3 秒 —— 客户看到的就是延迟。

| 节点 | 旧默认（`--delay 3`） | 现在 |
|---|---|---|
| 窗口上屏 | 3000 ms（睡满） | **~260 ms** |
| 首帧出画（720p 内嵌片段） | 3878 ms | **~410 ms** |
| 首帧出画（4K 社区片段） | —— | **~550 ms** |

改了三处：

- **`--delay` 默认改成 0**（`install.ps1` / `Setup.cs` / 播放器三处一致），
  自启命令里不再带这个参数。真正"和桌面抢时间"靠**让进程更早被拉起**，而不是让进程自己睡觉。
- **自启优先注册计划任务**（`AtLogon` 触发 + `Priority 7` + 登录触发），由任务计划服务在登录时拉起，
  不必等 `explorer` 把 Run 键轮询到。安装时先检测管理员权限，没有就直接走 Run 键
  （本机实测非管理员注册 `ONLOGON` 任务三种写法都是 `Access is denied`，
  所以"先试一次再回退"只会每次安装都打一行吓人的失败信息，改成先判断能力）。
- **播放器启动时把进程优先级抬到 `High`** —— 开机那几十秒有几十个进程在抢 CPU，
  4K 首帧解码不抬优先级就会被排到后面。
- 顺带修了一个**把延迟统计说错**的问题：`Stopwatch` 原来是在 `delay` **之后**才 `new` 的，
  所以日志里那句「进程启动到出画 878 ms」漏掉了前面所有真实开销。现在从 `Main` 第一行开始计时，
  并新增一行「窗口已显示 N ms」把"抢到屏幕"和"出画"两段分开量。

## 2. 裁切与居中：默认的 `cover` 每侧切掉了 11%

`Stretch = UniformToFill`（`--fit cover`）在长宽比不匹配时把画面推出窗口。
这块屏是 2560×1600（1.600），片源是 16:9（1.778）：铺满要放大 3840×2160 → 2845×1600，
**左右各裁掉 143px（11.11%）**。实测截图里，左上角的 `DeepSeek` logo 被推到边缘、
右上角的 `DEEPSEEK V1.0` 整块消失。一半画面在窗口外时，"居不居中"已经无从谈起。

新增四种贴合模式，**默认 `auto`**：

| 模式 | 行为 |
|---|---|
| **`auto`** | 铺满会裁掉 **≤1.2%** 时才铺满，否则整帧 + 氛围背景。两种情况都居中 |
| `ambient` | 整帧完整居中，画面之外用**同一段画面放大模糊**填满：不裁切、不留黑边 |
| `cover` | 铺满，超出部分裁掉 |
| `contain` | 整帧完整居中，留纯黑边 |

- 阈值 1.2% 是量出来的，不是拍的：第一版取 5%，结果报障那台机器的 1.4% 落在容忍区间内，
  `auto` 仍然选了铺满 —— **一个在报障场景上不触发的阈值，本身就是那个 bug**。
- `--fit auto` 需要真实分辨率才能算，所以拿到 `MediaOpened` 之前先按整帧画：
  早一帧显示不裁切的画面，好过显示一帧裁过的。
- 氛围背景：内嵌四段各带一张**构建期**用 ffmpeg 抽的图（`build/posters/`，抽 0.2s 而不是第 0 帧 ——
  片头常从黑场淡入，抽第 0 帧会得到纯黑图，那样等于没填）。社区片段没有内嵌图，
  **首次播放现场抽一张**并缓存到 `%LOCALAPPDATA%\BootAnimation\posters\`。没有 ffmpeg 就退化成黑边，播放不受影响。
- **提亮必须在 ffmpeg 侧做**：WPF 没有"亮度"效果，用"白底 + 原图当 OpacityMask"凑出来的提亮
  会把颜色洗成灰（实测边条 `R=G=B=103`），而素材的主视觉是青蓝/红色调 —— 洗掉颜色比不提亮更糟。
  改到 ffmpeg 的 `eq` 之后边条才有颜色（赛博朋克 `R=12 G=15 B=12`，ROG 片段是红色调）。
- **背景层透明度 0.55 是量出来的**：0.00 是纯黑（和 `contain` 无区别）、0.90 偏亮会抢画面，
  0.55 落在"看得出是画面本身的颜色、又不把视线从画面拉走"。

## 3. 顺手修的

- **卸载原来只删 Run 键**，计划任务留着 —— 客户点完卸载还会在开机时看到动画。
  现在 Run 键和计划任务一起清；安装器里取消"开机自动播放"时也两条都断。
- **`install.ps1` 里的 `$settings.StartDelay = [TimeSpan]::Zero` 是无效代码**：
  `New-ScheduledTaskSettingsSet` 返回的对象在 Windows PowerShell 5.1 上没有这个属性，
  赋值抛异常，而那个异常被外层 `catch` 当成"没有权限"记下来 —— 真正的失败原因被藏掉了。
  删掉它（新建任务本来就没有启动延迟）。

## 新增的验收工具

两条自检都能无界面跑，用来把上面两个"几何/时序断言"变成可回归的数字：

```
BootAnimation.exe --fit-check       # 打印 auto 的判定表，并断言阈值两侧
BootAnimation.exe --play --snapshot out.png --snapshot-at 1.6   # 把窗口真实渲染内容存成 PNG
```

`--snapshot` 用 `RenderTargetBitmap` 抓窗口视觉树，而不是在桌面上截屏：
"不居中 / 缺少一部分 / 黑边"全是窗口内部的布局结论，抓到用户眼睛看到的那一层才算数。
配套一个读 PNG 像素的小脚本（只用 Node 自带 zlib），因为 `contain` 的纯黑边和
未生效的氛围边条在肉眼上分不出来 —— 实测 `contain` 是 `R=0 G=0 B=0`，
而生效后的氛围边条是 `R=12 G=15 B=12`。

## 校验

```
BootAnimation-Setup.exe
  大小   34,383,360 字节 (32.79 MB)
```

`--selftest` 通过（四段内嵌视频解包后 sha256 与源文件一致）；`--fit-check` 退出码 0。

---

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
