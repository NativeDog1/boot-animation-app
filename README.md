# 开机动画（BootAnimation）

Windows 登录后立刻全屏播放一段片头动画，播完自动消失。**视频内嵌在 exe 里**，客户可以在内嵌的几段之间自己选。

- 单文件 **31.69 MB**（内嵌四段 2560×1440 片头），**不需要装 .NET 运行时**（用每台 Windows 都有的 .NET Framework 4.x）
- 自启方式默认写 `HKCU\...\Run` 键：**不需要管理员**，卸载就是删一个键值
- 起播不出画面时有明确提示（「正在加载…」/「播放失败：<原因>」/「20 秒无响应」），不会留一块黑屏让你猜

---

## 一、文件清单

| 文件 | 作用 |
|---|---|
| `src\BootAnimation.cs` | 播放器全部源码（一个文件，中文注释） |
| `src\Setup.cs` | 安装程序源码（把播放器内嵌进安装包） |
| `src\AssemblyInfo.cs` / `src\SetupInfo.cs` | 版本与发布者信息（**发布前请改成你自己的名字**） |
| `media\*.mp4` | 四段片头素材（2560×1440）—— **`build.ps1` 实际内嵌的就是这一套** |
| `build\app.ico` | 程序图标（256×256 PNG 封进 ICO） |
| `build.ps1` | 构建：同时产出播放器与安装包 |
| `test-install.ps1` | **发客户前必跑**：安装 → 卸载 → 再安装的往返自测 |
| `install.ps1` | 自用/开发用的脚本式安装（客户不需要它，用安装包即可） |
| `BootAnimation.exe` | 播放器本体（31.69 MB） |
| `BootAnimation-Setup.exe` | **发给客户的唯一文件**（31.71 MB） |
| `media-1440p\*` | 上一版 1440p 成品母带 + 4 张抽取帧（保留备查，**不参与构建** —— 构建只读 `media\`） |

---

## 二、快速开始

```powershell
# 1. 构建（产出播放器 + 安装包，并自动校验内嵌视频）
.\build.ps1

# 1.5 发客户前必跑：安装 → 卸载 → 再安装的往返自测
.\test-install.ps1

# 2. 自用安装（脚本方式；给客户请直接发 BootAnimation-Setup.exe）
.\install.ps1

# 3. 立刻试看 5 秒（不等到下次登录）
& "$env:LOCALAPPDATA\Programs\BootAnimation\BootAnimation.exe" --play --seconds 5

# 4. 换片头（弹选片窗口）
& "$env:LOCALAPPDATA\Programs\BootAnimation\BootAnimation.exe" --choose

# 5. 完全卸载
.\install.ps1 -Uninstall
```

安装时想直接指定片头、或想用计划任务（启动更早，需要管理员）：

```powershell
.\install.ps1 -Clip cyberpunk          # 安装并把片头设为赛博朋克
.\install.ps1 -Method task             # 用计划任务替代 Run 键（需要管理员）
.\install.ps1 -DryRun                  # 只打印将要做什么，不改任何东西
```

---

## 三、它到底动了你系统的哪些地方

安装只写三处，**不碰任何系统级设置**（不改 Winlogon、不改 Shell、不装驱动、不要管理员）：

| # | 位置 | 内容 | 卸载时 |
|---|---|---|---|
| 1 | `%LOCALAPPDATA%\Programs\BootAnimation\` | 复制的 exe | 整个删掉 |
| 2 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\BootAnimation` | 自启命令 | 删这个键值 |
| 3 | `%LOCALAPPDATA%\BootAnimation\` | 你的选片、日志、解出的视频缓存 | **保留**（重装后选片还在） |

第 2 项会出现在「设置 → 应用 → 启动」和任务管理器的「启动」页里 —— 也就是说**你自己随时能关掉它**，不用找我。

---

## 四、出问题怎么恢复

**动画没播 / 播一半卡住**：看日志 `%LOCALAPPDATA%\BootAnimation\boot-animation.log`，里面有 `MediaOpened`（含分辨率与「进程启动到出画」毫秒数）、`MediaFailed`（含原因）、超时记录。

**不想让它开机播了**：三种任选
```powershell
.\install.ps1 -Uninstall                     # 正规卸载
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name BootAnimation   # 只手删自启
# 或者：任务管理器 → 启动 → 把 BootAnimation 设为「已禁用」
```

**万一登录后屏幕被动画挡住、点不掉**（理论上不会：点一下或按 Esc 就退出，且 5 分钟强制结束）：
1. 按 `Ctrl+Shift+Esc` 打开任务管理器 → 结束 `BootAnimation.exe`
2. 还不行就 `Ctrl+Alt+Del` → 注销，再登录时它会因为「已经在跑」而被忽略（`IgnoreNew`）
3. 彻底修：安全模式里删掉上面那个 `Run` 键值

> 说明：本方案**刻意不采用**「改 `Winlogon\Shell` 让动画出现在桌面之前」那种做法。那种做法效果更接近真·开机动画，但包装脚本一旦写错，登录后就没有桌面，必须进安全模式才能修。当前方案最多是「动画挡了一下」，不会让你进不去系统。

---

## 五、命令行参数

| 参数 | 作用 |
|---|---|
| `--play` | 播放已选的片头（**开机自启用的就是这条**） |
| `--choose` | 弹出选片窗口，选完记住 |
| `--clip <id>` | 指定播放某一段：`brand` / `cyberpunk` / `awakening` / `startup` |
| `--file <路径>` | 播放任意外部视频（不限于内嵌的） |
| `--seconds <n>` | 播 n 秒后自动关闭 |
| `--delay <n>` | 显示前先等 n 秒（让桌面先铺好；自启默认 3 秒） |
| `--fit cover\|contain` | `cover` 铺满可裁边（默认）；`contain` 完整显示留黑边 |
| `--no-topmost` | 不置顶 |
| `--list` | 枚举内嵌片头，写到 `%LOCALAPPDATA%\BootAnimation\clips.txt` |
| `--selftest` | 无界面自检：解包内嵌视频、算哈希、写报告 |

不带任何参数双击 = 等同于 `--choose`（弹选片窗口）。

**随时可退出**：按 `Esc`、或点击画面任意处。

---

## 六、内嵌的四段片头

| id | 名称 | 大小 |
|---|---|---|
| `brand` | DeepSeek 品牌片头 | 4.78 MB |
| `cyberpunk` | DeepSeek 赛博朋克片头 | 6.78 MB |
| `awakening` | DeepSeek 数字角色苏醒 | 10.29 MB |
| `startup` | DeepSeek 启动问题 | 10.51 MB |

（上表是 `media\` 里那四段、也就是**真正被内嵌进 exe 的**体积，合计约 32.36 MB，
与 `BootAnimation.exe` 的 32.39 MB 对得上。`media-1440p\` 里的同名文件略小
（合计约 31.67 MB），那是上一版母带，不参与构建 —— 之前这里写的是它，已改正。）

想换成自己的片子：把 mp4 放进 `media\`，改 `src\BootAnimation.cs` 里 `Clips` 数组的条目和 `build.ps1` 里的 `$clips`，重新 `.\build.ps1`。

**格式建议**：H.264 + AAC 的 mp4 最稳（四段都是 2560×1440 24fps）。HEVC(H.265)、ProRes、部分 mkv 可能只有声音或黑屏。另外素材**必须先把索引表前置**，否则要整段读入才出画面：

```sh
ffmpeg -i 原片.mp4 -c copy -movflags +faststart 修好的.mp4
```

---

### 为什么是 1440p，以及为什么放弃了 AI 放大

素材母带是 1280×720。在 2560×1440 的屏幕上全屏播放意味着**放大 2 倍**，这是「看起来糊」的主因——编码参数救不了它，因为 720p 里没有那些细节。所以现在内嵌的是 1440p 成品。

走过的两条路，实测数据（闪烁 = 细节层的帧间跳动，越小越稳；锐度 = 拉普拉斯方差）：

| 做法 | 闪烁 | 锐度 | 四段体积 | 成本 |
|---|---|---|---|---|
| 直接用 720p（旧版） | 无 | 基准 23.8 | 8.65 MB | — |
| 逐帧 AI 放大（**已放弃**） | **6.18（严重）** | +860% | 56 MB | 每段 27 分钟 · 需 GPU |
| **母带锐化 + 放大（现用）** ✅ | **0.62（−90%）** | **+241%** | 31.7 MB | 四段共 11 秒 |

**AI 放大为什么被放弃**：逐帧图片模型对每一帧「想象」出来的微观纹理会随着画面运动整体更换，连起来就是严重闪烁。我试过只对「AI 多加的细节层」做 5 帧时间平滑，**实测只压掉 1.8%** —— 因为这种抖动和真实的帧间变化同量级，时间平均压不掉，抹平它等于白放大。

**真要补细节**，正确的工具是天生带时间维度的视频超分模型（BasicVSR++ / RealBasicVSR 这类），训练时就把相邻帧一起考虑。本机未装，8 GB 显存跑起来也紧张。

生成当前四段的命令（母带在 `dsh-dev\_clip-masters\`）：

```sh
ffmpeg -i 母带.mp4 -vf "unsharp=5:5:0.9:5:5:0.0,scale=2560:1440:flags=lanczos" \
  -c:v libx264 -crf 20 -preset medium -pix_fmt yuv420p -movflags +faststart -an 输出.mp4
```

> 关于 CRF：实测 CRF 20 与 CRF 18 的锐度差 **0.1%**（81.1 vs 81.2），但体积差 10 MB，所以选 20。

## 七、为什么用 C# + 自带编译器，而不是 Electron / Tauri

| 路线 | 需要什么 | 交付给客户是什么 |
|---|---|---|
| Tauri | 一整套 Rust 工具链 | 最小，但构建环境重 |
| Electron | node/npm | 安装包约 200 MB，每次开机起一个 Chromium |
| **C# + WPF + 自带 csc** ✅ | **什么都不用装** | **单文件 31.69 MB，任何 Windows 10/11 双击就跑** |

代价：Windows 自带的 `csc.exe` **只支持到 C# 5**（它自己的说明里写着），所以源码里不能用字符串插值、元组、`?.` 这些现代语法。对交付物没有影响。

---

## 八、诊断数据在哪

| 内容 | 路径 |
|---|---|
| 运行日志 | `%LOCALAPPDATA%\BootAnimation\boot-animation.log` |
| 自检报告（含四段哈希） | `%LOCALAPPDATA%\BootAnimation\selftest.txt` |
| 枚举结果 | `%LOCALAPPDATA%\BootAnimation\clips.txt` |
| 当前选中的片头 | `%LOCALAPPDATA%\BootAnimation\settings.txt` |
| 解包出来的视频缓存 | `%LOCALAPPDATA%\BootAnimation\clips\` |
| 安装清单 | `%LOCALAPPDATA%\BootAnimation\installed.txt` |

---

## 九、分发给客户

**发给客户的只有一个文件**：`BootAnimation-Setup.exe`（31.71 MB，播放器和四段视频都在它里面）。客户双击 → 选一段片头 → 安装 → 完成。全程**不需要管理员权限**，**不需要安装 .NET**。

安装程序做了五件事：

| 步骤 | 位置 |
|---|---|
| 释放播放器 | `%LOCALAPPDATA%\Programs\BootAnimation\BootAnimation.exe` |
| 记住选中的片头 | `%LOCALAPPDATA%\BootAnimation\settings.txt` |
| 注册开机自启 | `HKCU\...\CurrentVersion\Run\BootAnimation` |
| 建开始菜单快捷方式（打开选片窗口） | `…\Start Menu\Programs\开机动画.lnk` |
| 注册卸载入口 | `HKCU\...\CurrentVersion\Uninstall\BootAnimation` |

客户卸载：**设置 → 应用 → 已安装的应用 → 开机动画 → 卸载**。会删掉程序、自启、快捷方式和卸载条目，**保留客户的选片与日志**。

批量部署 / 远程推送可以静默安装：

```powershell
BootAnimation-Setup.exe /SILENT      # 退出码 0 = 成功
```

### 还差一步：代码签名

**目前这个安装包没有签名**，所以客户第一次运行会看到 Windows 的 SmartScreen 警告（「Windows 已保护你的电脑」），需要点「更多信息 → 仍要运行」。这没法用代码绕过，是微软的信任机制：

| 做法 | 代价 | 效果 |
|---|---|---|
| **OV 代码签名证书** | 约 $200–400/年，且现在强制要求硬件令牌或云 HSM | 警告消失、发布者名字可见；但仍要累积下载量养信誉 |
| **EV 证书** | 约 $400–700/年 | 建立信誉更快 |
| **上架 Microsoft Store** | 开发者账号一次性约 $19 | 商店代签，客户无警告 |
| **先不加签名** | 免费 | 客户要手动点「仍要运行」；下载量少时警告会一直有 |

拿到证书后这样签名（本机目前**没有 `signtool`**，需要先装 Windows SDK）：

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f 你的证书.pfx /p 密码 BootAnimation-Setup.exe
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f 你的证书.pfx /p 密码 BootAnimation.exe
```

**发布前记得改两处**：`src\AssemblyInfo.cs` 与 `src\SetupInfo.cs` 里的 `AssemblyCompany`（决定卸载列表里的「发布者」），以及 `src\Setup.cs` 顶部的 `Version` 常量；改完重跑 `.\build.ps1`。
