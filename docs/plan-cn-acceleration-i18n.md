# 计划书：国内网络加速通道 + 多语言（简体中文）支持

> 项目：RHI (ReShade HDR Installer) — WinUI 3 / .NET 8
> 分支：`feat/cn-mirror-i18n`（基于 `main`）
> 状态：**待开工**（决策已全部拍板，见 §2）
> 产出人：小白 · 版本 1.0

---

## 1. 目标与非目标

### 目标
1. **加速通道**：在设置页提供一个"下载源"按钮/开关，让从 GitHub 拉取组件（ReShade、RenoDX/Luma 插件、DLSS/Streamline、OptiScaler、DXVK、加装包等）可以走镜像站/加速前缀，失败自动回退直连。
2. **多语言**：给程序加 i18n 底座 + 首个中文（`zh-Hans`）语言包，覆盖主窗口骨架、设置页、主安装/更新流程、状态栏、详情面板 7 大区的标签与说明。

### 非目标（明确不做）
- 不加速任何**非 GitHub** 域名（Nexus / PCGamingWiki / reshade.me / nightly.link / discord 一律不动）。
- 不代理 `api.github.com`（版本检查、ETag、digest 查询保持直连，保证元数据可信）。
- **RHI 自身更新（AutoUpdateService 下载 exe）保持原样**，不进加速通道。
- 不做 FAQ 长文 / motd / 补丁说明 / README 的翻译（P2，未来再做）。
- 不做语言包热更新、不做运行时在线翻译。
- 不改版本号（`AssemblyVersion`/`FileVersion` 由主人发版前手动改）。

---

## 2. 已拍板的决策（17 项，全部来自主人回答）

| # | 决策 | 结论 |
|---|---|---|
| Q1 | 按钮语义 | 设置页常驻"下载源"开关/下拉；默认**直连**；失败自动回退 |
| Q2 | 注入层级 | 新增 `MirrorRewriteHandler : DelegatingHandler`，在共享 `HttpClient` 管道里重写 URL |
| Q3 | 分流规则 | releases/codeload/objects → 前缀代理；raw → 前缀代理优先、jsDelivr 兜底；**api.github.com 不代理**；nightly.link 不代理 |
| Q4 | 回退与熔断 | 单请求超时 15s；镜像失败 → 换下一个 → 全挂回退直连；同镜像连续失败 2 次熔断（仅会话内） |
| Q5 | 安全 | 下载完整性校验，**默认开启**，设置里可关掉；校验失败 → 丢弃 → 回退直连重下 → 仍失败则中止并提示 |
| Q6 | 多语言机制 | 自建 JSON 语言包 + `Loc.T(...)` 静态类，缺键回退英文并记日志 |
| Q7 | 切换生效 | 设置页下拉（跟随系统 / English / 简体中文）；**重启 RHI 生效**并提示 |
| Q8 | 覆盖范围 | P0 = 主窗口骨架 + 设置页全部 + 主安装/更新流程 + 状态栏 + 详情面板 7 大区标签与说明 |
| Q9 | 翻译管线 | **离线预生成**语言包并提交仓库；**不做人工审校**（靠自动校验兜底） |
| Q10 | 资源红线 | 显存峰值 ≤ **5.5 GB**，内存 ≤ 22 GB；`-ngl 99` 全量 offload；翻完即关服务 |
| Q11 | 术语 | 组件名保留英文；先写 `docs/i18n-glossary.md`；**必须读 Hy-MT2 的 README 并把用法写进文档**；短译优先 |
| Q12 | 校验开关 | 默认开启；范围 = release 资产 + 数据文件；数据源 = GitHub API `digest` / `contents` blob sha |
| Q13 | 镜像来源 | 小白实测连通性与速度后选 2-3 个；默认列表**由 manifest.json 下发**；设置里可填自定义前缀 + 测速按钮 |
| Q14 | 自更新 | **不加速**，保持原样 |
| Q15 | 工程流程 | 新建分支；commit/push 前先问主人；建 `CONTEXT.md` + ADR |
| Q16 | 交付顺序 | **M1 加速通道 → 验收 → M2 多语言**，串行 |
| 补充 | 翻译审校 | 主人明确：**小白不做过一遍人工审校**（见 §6.6 自动校验兜底） |

---

## 3. 现状事实（代码侦察结论）

| 事实 | 数据 | 影响 |
|---|---|---|
| 项目规模 | 306 个 `.cs`，约 113,466 行 | 全量 i18n 是巨型工程，必须分批 |
| UI 构建方式 | 全部 C# 代码构建（`DetailPanelBuilder.*`、`SettingsHandler.*`、`DialogService.*`），XAML 几乎不承载文案 | `.resw` 的 `x:Uid` 优势用不上 → 选 JSON 方案 |
| HTTP 集中点 | `App.xaml.cs` DI 注册的唯一共享 `HttpClient`，已包一层 `GitHubTokenHandler` | 天然的 URL 重写插入点 |
| 下载 URL 分布 | `github.com` 129 处、`api.github.com` 33 处、`raw.githubusercontent.com` 16 处、`nightly.link` 5 处，散落 154 个 service 文件 | 手改不现实，必须集中式 Handler |
| 远程数据也带 URL | `manifest.json` 含 `DownloadUrl`/`DownloadUrl32/64`/`DirectUrl`/`releaseApiUrl` | Handler 层重写可一并覆盖"数据里下发的地址" |
| 现有完整性机制 | 只有"内容 hash 比对决定是否更新"，**无发布者签名校验** | 镜像引入的是"多一个中间人"，需补 digest 校验 |
| digest 可用性 | **实测**：`api.github.com/repos/clshortfuse/renodx/releases/tags/snapshot` 的 assets 带 `digest: "sha256:…"` | 校验有权威数据来源，不必改 manifest 格式 |
| 设置持久化 | 全部塞在一个 `settings.json`，走 `GameNameService.LoadNameMappings()/SaveNameMappings()`（250 ms 防抖） | 新设置项按项目约定并入，不新建配置文件 |
| i18n 基础 | **零**：无 `.resw`、无 `ResourceManager`、无 `CultureInfo` 本地化逻辑 | 从零搭建 |
| 文案规模 | 去重候选串 1847 条（含日志/内部串），真实 UI 文案估 1100~1600 条 | P0 阶段目标 500~700 条 |
| 打包 | `publish.bat` 逐条 `copy` 内容文件；Inno Setup 用通配递归 `{app}\*` | 新增 `Lang\` 目录**必须**在 `publish.bat` 里显式 copy |
| 本地翻译环境 | `E:\ai\llamacpp`（llama.cpp b10356，含 `ggml-cuda.dll` + `cublas64_13.dll` CUDA 13）；模型 `Hy-MT2-7B-Q4_K_M.gguf` 4.6 GB；GPU RTX 5060 Laptop 8 GB（当前占用 1.6 GB） | 翻译可本地全 GPU 跑，无需联网、无需 torch |
| 系统 Python | `E:\python\python.exe`（无 torch；用标准库 `urllib` 即可，无需装包） | 工具脚本零依赖 |

---

## 4. M1 — 国内加速通道

### 4.1 架构

```
各 Service (129+ 处调用点，完全不改)
        │  GetAsync("https://github.com/.../releases/download/...")
        ▼
┌──────────────────────────────────────────────┐
│ HttpClient (App.xaml.cs, 单例)                │
│  ├─ MirrorRewriteHandler   ← 新增（最外层）    │
│  │     分流 → 重写 URL → 失败换镜像 → 熔断     │
│  └─ GitHubTokenHandler     ← 已有（内层）      │
│        api.github.com 才贴 token              │
└──────────────────────────────────────────────┘
        │
        ▼
   GitHub 直连 / 镜像1 / 镜像2 / jsDelivr 兜底
```

**核心设计**：镜像策略是**纯函数**（`MirrorPolicy`），Handler 只做"调用策略 + 重试 + 日志"。这样 90% 逻辑可以单元测试，不需要真网。

### 4.2 分流规则（`MirrorPolicy`）

| 原 URL | 是否走镜像 | 说明 |
|---|---|---|
| `github.com/{o}/{r}/releases/download/{tag}/{file}` | ✅ 前缀代理 | 主战场：所有 release 资产 |
| `github.com/{o}/{r}/archive/...`、`codeload.github.com/**` | ✅ 前缀代理 | 源码包 |
| `objects.githubusercontent.com/**`（release 跳转目标） | ✅ 前缀代理 | 需**就地重写**为代理 + 原 URL |
| `raw.githubusercontent.com/**` | ✅ 前缀代理优先，jsDelivr 兜底 | `manifest.json`、`dlss_manifest.json`、`RenoDXdb*.json`、`pcgw_data.json`、`engine-files/**`、Addons.ini 等 |
| `api.github.com/**` | ❌ 绝不代理 | 条件请求/ETag/限流/digest 必须直连可信 |
| `nightly.link`、`nexusmods`、`reshade.me`、`pcgamingwiki` 等 | ❌ | 非 GitHub 域，主人明确不做 |
| 自定义前缀本身 | ❌ 防递归 | 幂等保护：URL 已以某前缀开头则跳过 |

**前缀代理形态**：`{mirrorPrefix}` + 原始完整 URL（例 `https://ghfast.top/https://github.com/...`）。
**jsDelivr 形态**：`raw.githubusercontent.com/{o}/{r}/{ref}/{path}` → `https://cdn.jsdelivr.net/gh/{o}/{r}@{ref}/{path}`（仅作 raw 兜底）。

### 4.3 镜像列表来源

优先级（高 → 低）：
1. 主人自定义前缀（`settings.json`）
2. `manifest.json` 新字段 `"mirrorPrefixes": ["https://…/", …]`（远端下发，**不发版也能换**）
3. 代码内硬编码的兜底 1 个（防止 manifest 拉不到时功能失效）

`RemoteManifest` 加两个字段：`mirrorPrefixes`（`List<string>?`）、`mirrorEnabledDefault`（`bool?`）。旧缓存无此字段 → 走兜底，不报错。

### 4.4 回退 / 熔断 / 超时

```
尝试顺序：镜像1 → 镜像2 → … → 直连
单镜像：连接超时 10s，首字节 15s
成功判据：HTTP 2xx（且 Content-Length 与 HEAD 直连一致，若可得）
熔断：同一镜像本会话连续失败 2 次 → 跳过（10 分钟内），并记日志
回退可重试的条件：GET/HEAD 且响应体未开始消费（避免半截文件）
日志：CrashReporter.Log("[Mirror] host=… prefix=… result=ok|fail|fallback reason=…")
UI：仅状态栏一句 "加速通道不可用，已改用直连"（不弹窗轰炸）
```

### 4.5 完整性校验（默认开启）

| 对象 | 校验方式 | 数据来源 |
|---|---|---|
| release 资产（DLL / exe / 7z / zip） | `sha256` vs `asset.digest` | `api.github.com/repos/{o}/{r}/releases/tags/{tag}`（**不代理**），按 `tag+assetName` 建缓存表（内存 + 磁盘，24 h） |
| 数据文件（`manifest.json` 等 raw） | git blob sha（`sha1("blob {len}\0"+content)`）vs `contents` API 的 `sha` | `api.github.com/repos/{o}/{r}/contents/{path}`，带 ETag |
| 无法获得权威摘要的 URL | 降级为「Content-Length 比对 + 记录日志」，**不阻断** | — |

失败路径：`校验失败 → 删除临时文件 → 标记该镜像不可信 → 用直连重下 → 再失败则弹错误并中止安装`。
额外收益：避免 `ModInstallService` 因错文件 hash 不同而**反复触发更新循环**。

设置项：`VerifyDownloads`（默认 `true`）；关闭时在设置页显示黄色提示"你将失去对加速通道篡改的防护"。

### 4.6 新增 / 修改文件清单

**新增**
- `Services/Net/MirrorPolicy.cs` — 纯函数：`TryRewrite(uri, settings, out mirroredUri, out kind)`、`IsGitHubDownloadHost(...)`
- `Services/Net/MirrorRewriteHandler.cs` — DelegatingHandler（重写 + 重试 + 熔断 + 日志）
- `Services/Net/MirrorHealthTracker.cs` — 会话内失败计数/熔断表
- `Services/Net/DownloadIntegrityGuard.cs` — digest 获取与缓存、sha256 比对、blob sha 计算
- `Services/IMirrorHealthTracker.cs`、`Services/IDownloadIntegrityGuard.cs` — DI 接口
- `SettingsHandler.Download.cs`（`SettingsHandler` partial）— 设置页「下载」区 UI
- `RenoDXCommander.Tests/MirrorPolicyTests.cs` — 单测（分流/幂等/自定义前缀/兜底）
- `docs/mirror-eval.md` — 实测记录（连通性、速度、可用性，作为选型依据）

**修改**
- `App.xaml.cs` — HttpClient 管道加 `MirrorRewriteHandler`（注册在 `GitHubTokenHandler` 外层）
- `Services/GameNameService.cs` + `Services/IGameNameService.cs` — `DownloadSourceMode`（`Direct`/`Accelerated`）、`CustomMirrorPrefix`、`VerifyDownloads` 三项的 load/save + 迁移
- `ViewModels/MainViewModel.Settings.cs` + `MainViewModel.cs` — Get/Set/Resolve 三件套
- `Models/RemoteManifest.cs` — `mirrorPrefixes`、`mirrorEnabledDefault`
- `Services/ManifestService.cs` — 把 `mirrorPrefixes` 落到可读位置（含 `CacheLoad` 路径，遵守"双初始化路径"规则）
- `manifest.json`（仓库根）— 新增 `mirrorPrefixes` 字段（**发布时才推 main**，见 §8）
- `RenoDXCommander/RHI_PatchNotes.md` — 同轮更新
- `docs/adr/` — 见 §9

**不改**
- `publish.bat`（M1 无新内容文件）
- `MainWindow.xaml`（遵守"不加新 `x:Name`"约束；UI 用 C# 建）

### 4.7 任务拆分与验收

| 任务 | 内容 | 验收标准 |
|---|---|---|
| **M1.1 侦察与实测** | 用 `curl` 实测候选镜像（连通性 / 首页响应 / 实际拉一个 release 资产 5 MB / 拉一个 raw 文件 / 是否支持 Range / 是否支持 HEAD） | `docs/mirror-eval.md` 有表格 + 选定 2-3 个默认镜像 |
| **M1.2 策略纯函数** | `MirrorPolicy` + 单测 | `dotnet test` 全绿；覆盖 6 类 URL、幂等、自定义优先级、api 拒代理 |
| **M1.3 Handler 接入** | 重写 + 重试链 + 熔断 + 日志 | 断网/坏前缀场景自动回退直连；日志可读 |
| **M1.4 完整性校验** | digest 查询缓存 + sha256 + blob sha + 失败回退 | 故意改坏镜像文件 → 校验失败 → 直连重下成功；API 调用有缓存不爆限流 |
| **M1.5 设置与 UI** | 持久化 + 设置页「下载」区（模式下拉 / 自定义前缀 / 校验开关 / 测速按钮 / 状态提示） | 重启后设置保留；关闭校验出现警示；测速按钮给出各镜像延迟 |
| **M1.6 manifest 下发** | `RemoteManifest` 字段 + `BuildCards`/`CacheLoad` 双路径 | 断开网络置空 manifest 时走硬编码兜底 |
| **M1.7 全链路真机验证** | ReShade / RenoDX 插件 / DLSS / OptiScaler / DXVK / Addon 各装一次 | 见 §7 DoD |

### 4.8 M1 风险与对策

| 风险 | 对策 |
|---|---|
| 公益镜像寿命短，随时挂 | manifest 下发 + 自定义前缀 + 熔断 + 直连兜底；永不"只有镜像一条路" |
| 镜像不支持 `Range`/`HEAD`，破坏续传与长度比对 | 检测到不支持就降级为"单次完整下载"，仅告警不失败 |
| digest 查询拉爆 GitHub 限流（60/h 无 token） | 复用已有 `GitHubETagCache` + 24 h 磁盘缓存；无 token 时**降低校验频率**并记录（不阻断安装） |
| 镜像返回 200 但内容是 HTML 拦截页 | Content-Length 比对 + 文件头校验（`.dll` 需 `MZ`、`.7z` 需签名）→ 判为失败 |
| `HttpClient` 已带 10 min `Timeout`，直接加 15 s 会伤害大文件 | 超时只作用于"连接/首字节"（`ConnectTimeout` + 首包探测），不限制整包时长 |
| 请求 Body 不可重放导致重试异常 | 只对 `GET`/`HEAD` 做镜像重试，其他方法原样放行 |

---

## 5. M2 — 多语言（zh-Hans）

### 5.1 机制（English-as-key）

语言包用 **英文原文作 key**，而不是人工语义 key：

```json
// RenoDXCommander/Lang/zh-Hans.json
{
  "Download source": "下载源",
  "Accelerate GitHub downloads (mirror)": "加速 GitHub 下载（镜像）",
  "Installed": "已安装",
  "Failed to install {0}": "安装 {0} 失败"
}
```

代码侧：`Loc.T("Installed")` / `Loc.T("Failed to install {0}", name)`。

**为什么这么选**（3 个理由）：
1. **抽取成本≈0**：不用给 1500 条串造语义 key，改代码就是"把字面量包进 `Loc.T()`"。
2. **缺键自动回退**：中文包里没有这条 → 直接显示英文原文，永远不会出现空白/方块。
3. **翻译表可读可审**：主人抽查时看到的是 `"Installed": "已安装"`，一眼能判断对错。

代价：英文原文一改，该条翻译就"失配"（回退英文）。用 `validate.py` 对比 `en.json` 基线即可列出失配清单，重译那几条就行。

### 5.2 新增 / 修改文件清单

**新增**
- `Localization/Loc.cs` — 静态门面：`Culture`、`T(key, args...)`、`Load(lang)`、`MissingKeyCount`；UI 线程安全；未初始化时返回原文
- `Localization/LanguageService.cs` — 解析"跟随系统"、枚举可用语言、读 `settings.json`、加载语言包、写日志
- `Localization/ILanguageService.cs`
- `RenoDXCommander/Lang/en.json` — 基线（英文，用于对齐与失配检测；可由工具生成）
- `RenoDXCommander/Lang/zh-Hans.json` — 翻译产物
- `tools/i18n/extract.py` — 扫描 `.cs` 候选串 → 生成 `worklist.json`
- `tools/i18n/translate.py` — 调本地 llama-server 批量翻译 → 生成 `zh-Hans.json`
- `tools/i18n/validate.py` — 自动校验（§6.6）+ 缺键/失配报告
- `tools/i18n/run-llama-server.ps1` — 带资源监控的启停脚本
- `docs/i18n-glossary.md` — 术语表（**要先给主人过目**）
- `docs/i18n-pipeline.md` — Hy-MT2 用法留档（**主人明确要求**，见 §6.1）
- `SettingsHandler.Language.cs` — 设置页「语言」区 UI

**修改**
- `App.xaml.cs` — 启动早期 `LanguageService.Apply()`（早于 `MainWindow` 构建）
- `Services/AppPaths.cs` — `LangDir` 属性（`ExeDir/Lang`）
- `Services/GameNameService.cs` + `IGameNameService.cs` + `MainViewModel.Settings.cs` — `Language`（`system`/`en`/`zh-Hans`）
- **P0 范围内所有 UI 文案调用点**（`DetailPanelBuilder.*`、`SettingsHandler.*`、`MainWindow.*`、`DialogService.*`、状态栏）— 字面量包 `Loc.T(...)`
- `publish.bat` — `xcopy /s /y "%SRC%\Lang" "%OUT%\Lang\"`
- `RenoDXCommander.csproj` — `Lang/*.json` 设为 `Content` + `CopyToOutputDirectory`（供调试运行也读得到）
- `RenoDXCommander/RHI_PatchNotes.md` — 同轮更新

### 5.3 分批：P0 拆成 3 个翻译波次

**翻译不一次做完**（主人要求：分 3 次完成，每波都等得起）。每波是一条完整流水线：**翻译 → 校验 → 代码接线 → publish 走查 → 主人点头 → 下一波**。任何一波中断，之前交付的部分都是完整可运行的中文界面，不会出现"半截界面"。

| 波次 | 范围 | 预估条数 | 模型耗时预算 | 交付可见效果 |
|---|---|---|---|---|
| **W1 外壳** | 主窗口骨架/导航/搜索、状态栏、设置页全部（含新增「下载」区、「语言」区） | ~150–220 | ≤ 12 min | 中文外壳：窗口、设置页、状态栏可读 |
| **W2 流程** | 主安装/更新流程（按钮、进度、确认框、结果提示）、错误与提示弹窗、Cog/FAQ 入口文案 | ~180–250 | ≤ 12 min | 装组件的整条链路中文可读 |
| **W3 详情面板** | 详情面板 7 大区（ReShade / RenoDX / DLSS / OptiScaler / DXVK / HDR / Extras）的标题、开关标签、说明 | ~300–400 | ≤ 18 min | 详情面板中文可读，P0 收官 |

每波的硬性纪律：
1. **单次模型运行 ≤ 20 分钟**。到点就落盘 `zh-Hans.partial.json` 收工，下次续跑（断点续跑是 `translate.py` 的必备功能，不是可选项）。
2. 每波结束必须 `validate.py` **硬失败 = 0** 才允许进入代码接线。
3. **只接线本波条目**。没翻到的条目继续显示英文——English-as-key 天然支持，界面永远不会空白。
4. 每波结束 `publish.bat` + 中文实机扫一眼（见 §5.5），再问主人是否提交。
5. 三波合计仍属 P0 范围；**不允许把 P1/P2 的内容提前塞进某一波**（那会让某一波突然变长）。

| 后续批次 | 范围 | 本次 |
|---|---|---|
| P1 | 详情面板各功能卡片细项、Extras/NeuralRendering 深度文案、工具弹窗 | ❌ 后续 |
| P2 | FAQ 长文、motd、补丁说明、README/指南 | ❌ 后续 |

**不翻的东西**（写进 glossary 作为规则）：
- 组件名与固有名：ReShade / RenoDX / Luma / OptiScaler / DLSS / DLSSD / DLSSG / FSR / XeSS / Streamline / DXVK / HDR / NVAPI / ReLimiter / Display Commander / Frame Gen / MFG / Reflex / NVIDIA / Steam / Epic / GOG / Xbox / Nexus
- 渠道名：`Stable` / `Nightly` / `Development` / `Global` / `Custom` / `Select` / `Off`
- 文件名 / 路径 / 版本号 / 注册表项 / ini 键名 / 日志文本（`CrashReporter.Log` 一律英文，便于排障）
- 游戏名（保持原样）

### 5.4 语言切换

- 设置页「语言」下拉：`跟随系统`（默认）/ `English` / `简体中文`
- 选择后 → 保存 → 提示"重启 RHI 后生效"（ContentDialog，带"立即重启"按钮）
- `LanguageService` 用 `CultureInfo.CurrentUICulture` 判断系统语言；`zh`/`zh-Hans`/`zh-CN` → 简体中文；其他 → English
- **数字/日期/浮点解析一律保持 `InvariantCulture`**（现有代码已如此，不得因中文改动，否则 ini 解析会炸）

### 5.5 中文排版风险控制

- 短译优先：按钮文案 ≤ 4 字（如 `Install` → `安装`、`Apply` → `应用`）
- `validate.py` 检查"中文长度 > 英文长度 × 1.8"的条目 → 产出告警清单，逐条改短
- 固定宽度区域（详情面板折叠标题、设置导航栏）优先用"短译 + 完整文案放 Tooltip"
- 每个 UI 页翻完用 `publish.bat` 出包，实机截图检查溢出（不做本机自动检查）

### 5.6 任务拆分与验收

| 任务 | 内容 | 验收 |
|---|---|---|
| **M2.1 底座** | `Loc` + `LanguageService` + `Lang/` 打包链路 + 设置项 | 切到 en 一切正常；切 zh-Hans 且语言包为空时界面仍是英文不崩 |
| **M2.2 候选抽取** | `extract.py` 出 worklist + 人工/规则筛掉日志与内部串 | worklist 条数与预估(500~700)同量级，日志串被排除 |
| **M2.3 翻译管线** | 起 llama-server（§6.2）+ `translate.py` + 术语注入 | 抽 20 条试译，人工（主人或小白目视）判断可读 |
| **M2.4a W1 翻译 + 接线** | 外壳文案（窗口/导航/设置页/状态栏）翻译 → 校验 → 接线 | 硬失败 0；中文外壳可用；无缺键 |
| **M2.4b W2 翻译 + 接线** | 流程与弹窗文案翻译 → 校验 → 接线 | 硬失败 0；安装/更新链路中文可用 |
| **M2.4c W3 翻译 + 接线** | 详情面板 7 大区文案翻译 → 校验 → 接线 | 硬失败 0；详情面板中文可用；P0 收官 |
| **M2.5 P0 代码接线** | 字面量包 `Loc.T`（按文件分批） | 编译零新增 nullable 警告；`publish.bat` 成功 |
| **M2.6 实机走查** | 中文下跑完整安装流程 | 按钮/标签无溢出、无截断、无空白；截图留档 |
| **M2.7 文档** | glossary / i18n-pipeline / PatchNotes / ADR | 主人能照着再加一门语言 |

---

## 6. 翻译管线（Hy-MT2-7B + llama.cpp）

### 6.1 模型用法（已读 `E:\ai\llm\Hy-MT2-7B\README_CN.md`，结论写进 `docs/i18n-pipeline.md`）

关键事实：
- Hy-MT2 7B，33 语种互译，**没有默认 system prompt**（不要自己加 system 消息）。
- README 推荐 7B 采样参数：`temperature 0.7 / top_p 0.6 / top_k 20 / repetition_penalty 1.05 / max_tokens 4096`。
- 官方指令模板（默认翻译）：
  ```
  将以下文本翻译为 简体中文，注意只需要输出翻译后的结果，不要额外解释：

  {source_text}
  ```
  （`source_lang`/`target_lang` 用语言**全称**：中文写"简体中文"/"中文"，英文写 "English"）
- 另有 **Terminology**（术语参考）、**Delimiters**（保留等量分隔符）、**Structured Data**（禁止翻译占位符/键名）三类模板 —— 本管线用 **Terminology + Structured-Data 混合版**，把 glossary 与占位符规则一起喂进去。
- llama.cpp 用法：`--jinja`；README 警告 GGUF 依赖 STQ kernel（llama.cpp PR #22836）→ **开工第一步必须做 smoke test**，若本地 b10356 输出乱码/复读，改用官方 PR 构建或退回自带 chat 模板。

### 6.2 服务启动（受资源红线约束）

```powershell
# tools/i18n/run-llama-server.ps1 的核心命令
E:\ai\llamacpp\llama-server.exe `
  -m E:\ai\llm\Hy-MT2-7B\Hy-MT2-7B-Q4_K_M.gguf `
  --host 127.0.0.1 --port 8099 `
  --jinja -ngl 99 -c 4096 --parallel 2 `
  --temp 0.7 --top-p 0.6 --top-k 20 --repeat-penalty 1.05 `
  -n 512 --no-warmup
```

- 显存红线 **5.5 GB**：脚本每 500 ms 采样 `nvidia-smi --query-gpu=memory.used`，超线立即 `--parallel 1` → 仍超则 `-ngl 24`（部分 offload）→ 仍超则 `-ngl 0`（纯 CPU，慢但绝不过线）。
- 内存红线 22 GB：`-ngl 99` 下权重在 VRAM，进程 RSS 约 5 GB，天然满足；脚本同时记录峰值供留档。
- 翻完（或中断）**必须**杀掉进程，不常驻。
- 调用方式：`POST http://127.0.0.1:8099/v1/chat/completions`，body 里只放一条 user 消息（无 system），`max_tokens=512`（短句够用，加快速度）。

### 6.3 批量策略

- 每条串独立请求（不拼接、不批量塞进一次请求）：**避免模型把两条串合并或漏译**，且失败可单条重试。
- 并发 2（与 `--parallel 2` 对齐）。
- **单次运行上限 20 分钟**（配合 §5.3 的三波拆分）：到点保存并退出，绝不"再等一会儿就翻完"。
- 断点续跑：`translate.py` 每翻 N 条（建议 20）落盘一次 `Lang/zh-Hans.partial.json`，重跑自动跳过已完成条目；三波共用同一个 partial 文件，波次只决定"这次翻译哪些条目"。
- 重试：解析失败/空输出 → 重试 2 次（温度降到 0.3）→ 仍失败记为 `untranslated` 并保留英文。

### 6.4 Prompt（最终形态）

```
参考下面的翻译：
ReShade 翻译成 ReShade
DLSS 翻译成 DLSS
Framerate 翻译成 帧率
…（glossary 全量）
将以下文本翻译为 简体中文，注意只需要输出翻译后的结果，不要额外解释。
必须原样保留 {0} {1} {2} 这类占位符、%s %d 类格式符、\n 换行、以及所有英文文件名。
文本长度尽量不超过原文的 1.8 倍。

{source_text}
```

### 6.5 术语表（`docs/i18n-glossary.md`，先给主人过目）

内容为三列：英文 / 中文 / 类型（保留原文 · 译名 · 风格规则）。
规则集：组件名保留、渠道名保留、日志不译、UI 短译、单数"主"复数统一、按钮用动词原形（"安装""更新""卸载"）。

### 6.6 无人工审校 → 自动校验兜底（`validate.py`）

主人明确不做人工过稿，因此用硬性机器校验替代：

| 检查 | 规则 | 失败处理 |
|---|---|---|
| 键完整性 | zh 的键集合 ⊇ en 基线 | 报告缺失清单 |
| 占位符一致 | `{0}`/`{1}`/`%s`/`\n` 数量与顺序必须相同 | **硬失败**，回退英文并记日志 |
| 禁止残留英文整句 | 译文含 ≥ 3 个连续英文单词（专有名词白名单除外） | 告警，人工/规则复核 |
| 长度 | 中文长度 ≤ 英文 × 1.8 | 告警 |
| 空/重复值 | 空译文、与原文完全相同（除白名单）、同一中文对应两个英文 key 的重复值 | 告警 |
| 标点 | 中文用全角标点，句末不加英文句点 | 告警 |
| 危险字符 | `"\` 引号/反斜杠未转义、JSON 非法 | **硬失败** |

产物：`tools/i18n/report-zh-Hans.md`（硬失败=0 才允许进入 M2.5）。

---

## 7. 验收清单（Definition of Done）

**M1**
- [ ] 设置页出现「下载」区：模式下拉 / 自定义前缀 / 完整性校验开关 / 测速按钮，重启后保留
- [ ] 开启加速后，ReShade、RenoDX 插件、DLSS/Streamline、OptiScaler、DXVK、Addon 均能从镜像装成功
- [ ] 故意填一个坏前缀 → 自动回退直连，状态栏提示一次，不刷屏
- [ ] 相同镜像连续失败 2 次后被跳过（日志可见）
- [ ] 完整性校验开启时，篡改文件被拒；关闭时给出警示
- [ ] `api.github.com` 请求**不经过**镜像（日志可见 host）
- [ ] `dotnet test` 全绿（新增 `MirrorPolicyTests`）
- [ ] `publish.bat` 通过，`RHI.exe` 可运行

**M2**
- [ ] 设置页「语言」区：跟随系统 / English / 简体中文，选择后提示重启
- [ ] 中文下主窗口、设置页、安装流程、状态栏、详情面板 7 大区标题与说明全部为中文，无英文残留（白名单除外）
- [ ] 无溢出/截断（截图留档）
- [ ] 缺键时回退英文且不崩
- [ ] 数字/浮点/日期解析行为与英文模式完全一致（ini 读写回归）
- [ ] `validate.py` 硬失败 = 0
- [ ] 翻译按 **W1 / W2 / W3 三波**交付，每波独立验收通过后才开始下一波
- [ ] 每波单次模型运行 ≤ 20 min，中断后能续跑
- [ ] `publish.bat` 把 `Lang/` 带到输出目录，Inno 安装包内含 `Lang/`

**通用**
- [ ] `RHI_PatchNotes.md` 已在同一轮更新（`### New`/`### Changes`）
- [ ] 未改版本号
- [ ] 未在 `MainWindow.xaml` 新增 `x:Name`
- [ ] 未新增 nullable 警告
- [ ] commit / push 前已征得主人同意

---

## 8. 工作流约束（来自 `CLAUDE.md`，必须遵守）

1. 在 `feat/cn-mirror-i18n` 分支上做，**不碰 main**；commit/push 前一律先问主人。
2. 每次改代码后跑 `publish.bat`（主人真正运行的是发布输出）。
3. 平台恒为 `x64`；publish 不加 `--no-restore`。
4. 新增"exe 旁边读取的文件"（`Lang/*.json`）**必须**加进 `publish.bat`。
5. `manifest.json` 的改动：先 `python tools/check-manifest.py`，只做定点修改，不重排、保持 CRLF。**manifest 改动推 main 即等于上线**，所以 M1.6 期间先在分支本地验证，推送时机由主人定。
6. 大工程用 subagent 分担：
   - M1.2/M1.4 → 1 个 subagent（纯逻辑 + 单测）
   - M2.5 的 P0 文案接线 → 按文件分 2 个 subagent（`DetailPanelBuilder.*` / `SettingsHandler.*+DialogService.*`）
   - 主 agent 负责架构、Handler 接入、UI、集成与验收
7. 日志统一 `CrashReporter.Log("[ClassName.MethodName] …")`。

---

## 9. 需要新建的文档（本计划的一并产出）

| 文件 | 作用 |
|---|---|
| `CONTEXT.md` | 术语表（镜像通道 / 加速前缀 / 语言包 / 完整性校验 / 直连 ...） |
| `docs/adr/0001-mirror-url-rewrite-in-http-handler.md` | 为什么在 DelegatingHandler 层重写，而不是改各 service |
| `docs/adr/0002-download-digest-verification.md` | 为什么用 GitHub digest 校验、为什么默认开启、失败为什么要阻断 |
| `docs/adr/0003-json-language-pack-english-as-key.md` | 为什么不用 `.resw`、为什么用英文原文当 key |
| `docs/i18n-glossary.md` | 术语表（主人过目后翻译才开工） |
| `docs/i18n-pipeline.md` | Hy-MT2 + llama.cpp 完整用法留档（含 smoke test 记录、参数、显存实测） |
| `docs/mirror-eval.md` | 镜像实测数据 |

---

## 10. 遗留待主人确认（3 项，均不阻塞开工）

1. **设置页「下载」与「语言」是独立分节还是并入现有 General 节**？小白倾向**独立两节**（将来加语言/换镜像都好找）。
2. **测速按钮**要保留吗（多一个开发点，但对国内用户很实用，能自己判断哪个镜像快）？小白倾向保留。
3. 渠道名 `Stable`/`Nightly`/`Development` 在中文界面**保留英文**可以吗喵（业界习惯，保留更利于排障）？
