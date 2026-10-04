# UEFI Boot Runtime — 设计、接入点与 Fallback

> 状态：**未实现**。这份文档是设计，不是完成报告。
>
> 客户端的 System Status 里 `UEFI Boot Entry` 与 `UEFI Animation` 两项显示
> **「未实现」**（而不是绿色的 ● Ready）。这是刻意的：一个硬编码的"就绪"会让用户
> 失去唯一的诊断入口，而这里涉及的是一条**能把机器变得开不了机**的路径。

---

## 1. 为什么需要它，以及它解决的不是同一个问题

现在的实现只能做到：

```
固件 → Windows Boot Manager → Windows → 登录 → Run 键/计划任务 → BootAnimation.exe → 动画
```

用户要的是"接近 Windows 7 那种体验"：**系统启动流程本身就包含动画**，而不是
Windows 已经起来之后再由一个普通应用补播。这中间的差距不是优化能弥补的 ——
只要动画由登录后的用户进程播放，就永远晚于桌面。

要在 Windows Boot Manager **之前**播动画，只有一条路：往固件启动顺序里插一个
自己的 UEFI Application，它先画动画、再链式加载 `bootmgfw.efi`：

```
固件 → DSH Boot Runtime (UEFI) → 动画 → bootmgfw.efi → Windows
```

---

## 2. 参考实现：AnimeBoot 做了什么（以及不能照抄什么）

`helloyork/AnimeBoot` 证明了这条路可行，机制如下：

| 环节 | 做法 |
|---|---|
| 形态 | 单个 EDK2 UEFI Application（`UEFI_APPLICATION`，IA32\|X64），约 23.5 KB |
| 取得控制权 | 往固件启动顺序前列插入自己的启动项，不替换任何文件 |
| 注册 | `bcdedit /copy '{bootmgr}'` → `/set path \EFI\AnimeBoot\AnimeBoot.efi` → `displayorder … /addfirst` |
| 绘制 | `gBS->LocateProtocol(gEfiGraphicsOutputProtocolGuid)` + `Gop->Blt`，32bpp |
| 模式 | **不调用 `SetMode`** —— 用固件当前已经设好的模式，链式加载前恢复 |
| 链式加载 | `gBS->LoadImage` + `StartImage` 加载 `\EFI\Microsoft\Boot\bootmgfw.efi`；**从不改名/替换 bootmgfw.efi，也不动 BOOTX64.EFI** |
| 资源格式 | 自定义 `.anim` 容器：96 字节头 + JSON manifest + 帧索引表 + **未压缩的整帧** |
| 资源生产 | Python `abtool`：GIF/APNG/PNG/MP4 → BMP 序列 → 拼容器（无编码器、无帧间压缩） |
| 内存 | 只保留两个 `w*h*4` 缓冲；分片 32 KB 读（某些固件 >2 MB 的读会失败） |

### 为什么不能照抄它的**资源管线与安全模型**

| 问题 | 事实 | 对一个要交付给客户的产品意味着 |
|---|---|---|
| 帧不压缩 | 220 帧 300×300 的动画 = **79 MB**，让开机多花 **6.7 秒** | Windows 默认 ESP 只有 100 MB，装不下；而且没人愿意为一段片头等 6.7 秒 |
| 无看门狗 | 没有 `SetWatchdogTimer`、没有启动超时、没有自愈 | 唯一的兜底是"固件加载失败时继续下一个启动项" |
| Secure Boot | 未签名二进制会被直接拒绝；文档给的两条路（替换 PK/KEK/DB，或 shim+MokManager）一条是接管机器、一条没实现也没验证 | 不能这样交付 |
| Windows 更新 | 仓库里完全没有讨论 ESP/BCD 被服务更新改写的情况 | 这是长期的维护负担，而且失败后果是开不了机 |
| 成熟度 | 4 个 commit、13 star、1 个未回复的 issue、无 LICENSE 文件；维护者自己的发布说明写着"在上一个提交之前什么都不能构建、什么都不能播" | 只能当**设计草图**，不能当模板 |

结论：**机制值得借用，资源管线与安全模型必须重做**。所以下面是一份"如果要接入，
必须满足什么条件"的设计，而不是一次实现。

---

## 3. 本项目的接入点（已预留，未启用）

代码里已经为这条路径预留了三处**互不耦合**的边界：

| 预留点 | 位置 | 现状 |
|---|---|---|
| 启动模式枚举 | `BootRuntime.cs` 的 `BootMode` | 已有 `Startup` / `Resume` / `Manual`；UEFI 不是这个枚举的一员，因为 UEFI 阶段**根本不运行这个 exe** |
| 资源准备 | `AnimationInfo.Package*` 字段 + `PackageState` | 字段已定义（`NotPrepared` / `Ready` / `Stale` / `Failed`），但**没有实现打包器** —— 如实反映"运行时就绪资源尚未落地" |
| UEFI 兼容性判定 | `AnimationInfo.UefiCompatible` | 已有真实判据（本地文件、≤3840×2160、≤64 MB），仅用于**显示**，不驱动任何行为 |
| 诊断项 | `Diagnostics.cs` 的 `UEFI Boot Entry` / `UEFI Animation` | 固定报 `NotImplemented`，带说明 |

**关键决定**：UEFI Runtime 必须是一个**独立模块**（未来新增 `Runtime/UefiRuntime.cs`
与之配套的 EFI 应用工程），它：

- 不与 `BootRuntime`（Windows 侧）共享代码 —— 那边的依赖（WPF / Media Foundation /
  .NET）在 UEFI 里一个都没有；
- 不与 `Shell`（UI）有任何调用关系 —— UI 只负责"安装/应用/查看状态"；
- 自己的资源格式由**安装时**的打包器产出，开机时只做顺序读取。

---

## 4. 资源管线（必须先做对，否则不值得做）

AnimeBoot 的失败教训很明确：**未压缩整帧不可交付**。本项目要求的管线：

```
用户在界面点「Apply」
   ↓
Animation Compiler（安装时，一次性、可失败、可重试）
   1. 输入校验（容器、编解码器、时长上限、体积上限）
   2. 抽帧 + 缩放 + 量化到 GOP 友好格式
   3. **帧间差分 + 轻量压缩**（关键差异点：不是裸帧）
   4. 写 manifest：分辨率 / FPS / 帧数 / 每帧时长 / 总时长 / 内存峰值预算
   5. 写 Frame Index（随机访问用的偏移表）
   6. sha256 + 体积
   7. **试播**（用 Windows 侧的播放器把这份资源完整过一遍）
   8. 标记 Ready
   ↓
下一次冷启动：UefiRuntime 直接读 Prepared Runtime Resource
```

开机时**禁止**做：转码、编码器初始化、ffmpeg 调用、目录扫描、缩略图生成、下载。
这一条与 Windows 侧 `BootRuntime` 的约束完全一致，只是约束更严（UEFI 阶段连文件系统
驱动都只有 FAT）。

**体积目标**：一段 5–10 秒、1440p 的片头，预编译资源应当控制在 **10–20 MB** 以内，
从而使开机增加的时间控制在 **1 秒以内**。做不到这一点就不应该上 UEFI 阶段。

---

## 5. Fallback / Recovery（这条不满足就绝不上线）

按优先级，每一层都必须能独立地把机器带回 Windows：

| 层 | 机制 | 失败后果 |
|---|---|---|
| L0 · 不碰原启动项 | 只 `bcdedit /copy` 新增一项，**绝不改名/替换/删除** `bootmgfw.efi` 与 `BOOTX64.EFI` | 用户可随时在固件里改回启动顺序 |
| L1 · 固件兜底 | 自己的 `.efi` 加载失败时，固件自动继续下一个启动项 | 这条是**现有实现唯一的兜底**，不够 |
| L2 · 自超时 | UEFI 应用内部看门狗：无论动画/资源出什么问题，N 秒内必须链式加载 `bootmgfw.efi` | 动画卡住也不会卡死启动 |
| L3 · 尝试计数自愈 | 在 ESP 上记"连续启动动画次数"，超过阈值（例如 3 次）就**自动把自己从启动顺序里摘掉**并照常启动 | 资源损坏导致的循环不会变成永久开不了机 |
| L4 · 资源校验 | 开机前校验 manifest 与 sha256；不符则**跳过动画**直接链式加载 | 半个文件不会导致黑屏卡住 |
| L5 · 一键卸载 | 界面上能一键移除自己的启动项与 ESP 上的文件；并在安装时 `bcdedit /export` 备份 BCD | 用户可自救 |
| L6 · 文档化的人工恢复 | 安装前明确告知"固件 → 重置启动顺序"这条退路 | 最后一道 |

**还需要解决、目前没有答案的**：
- Secure Boot：自己签名的二进制会被拒。要么走 Microsoft UEFI CA 签名的 shim +
  MokManager（需要用户交互），要么做 EV 证书 + Partner Center 提交。两者都不是
  "贴个文件"就能完成的事。
- BitLocker：ESP 上的改动是否会触发恢复密钥提示，未验证。
- Windows 功能更新：服务更新会重建 ESP/BCD，需要验证并给出"更新后自动重注册"的路径。
- Fast Startup / hiberboot：AnimeBoot 完全没有处理，行为未知。

---

## 6. 唤醒（Resume）与冷启动是两件事 —— 已实现的是唤醒侧

冷启动会重新执行固件与 Windows Boot Manager；**S3/S4 恢复是内存镜像还原，不会重新执行
UEFI，也不会重跑登录流程**。所以：

```
冷启动  固件 → [UEFI 动画，未实现] → bootmgfw → Windows
唤醒     Resume → [ResumeRuntime：电源事件监听，已实现] → 桌面
```

`ResumeRuntime.cs` 已经落地，监听并处理：

- `PBT_APMRESUMEAUTOMATIC`（0x0012）—— 系统自动恢复（含现代待机 S0）
- `PBT_APMRESUMESUSPEND`（0x0007）—— 因用户操作恢复
- `PBT_APMSUSPEND`（0x0004）—— 即将睡眠

三者都要处理：只监听 `SUSPEND` 会漏掉自动恢复；只监听 `RESUME` 会在部分机型上不触发。
唤醒处理丢到 `DispatcherPriority.Background`，不在电源广播的同步路径上建窗口 —— 否则会
拖住整个电源转换。

**如实说明的限制**：本进程退出后就不再监听。也就是说"唤醒也自动全屏播一段"目前只在
界面运行时有效；要做到"常驻监听"，需要一个轻量常驻进程（或让本程序的某个模式常驻），
这是下一版的事。

---

## 7. 上线判据（checklist）

UEFI Runtime 只有同时满足以下全部条件才允许进入发布版本：

- [ ] 资源管线产出 ≤20 MB / 5–10 秒片段，且开机增量 <1 秒（在真实机器上实测）
- [ ] L2 自超时 + L3 尝试计数自愈 + L4 资源校验 三层都实现并有测试
- [ ] Secure Boot 开启时**要么能正常签名启动，要么明确拒绝安装**（绝不静默失败）
- [ ] BitLocker 开启的机器上验证过不触发恢复提示
- [ ] Windows 功能更新后自动重注册的路径验证过
- [ ] 一键卸载与 BCD 备份恢复验证过
- [ ] 在**至少 3 台不同厂商的真实机器**上冷启动 / 重启 / 快速启动各验证一遍
- [ ] 安装前明确告知风险与退路

在第 1 项（体积与开机增量）和第 2 项（三层兜底）完成之前，剩下的都不用做 ——
因为一个"能播但开机变慢、还可能开不了机"的动画，比现在这个"晚几秒但绝对安全"的版本差。
