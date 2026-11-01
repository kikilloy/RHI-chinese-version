# i18n 术语表（草案 · 待主人过目后开始翻译）

> 用途：喂给 Hy-MT2 的 Terminology 指令 + 校验脚本的白名单。规则冲突时以"保留原文"优先。

## 1. 必须保留英文（不译）

| 类别 | 词条 |
|---|---|
| 组件/项目 | ReShade、RenoDX、Luma、OptiScaler、OptiScaler DLSS-NR、DLSS Swapper、DXVK、dgVoodoo2、Special K、ENB、REFramework、ReLimiter、Display Commander、Lilium HDR、Ultra+、RHI |
| 图形技术 | DLSS、DLSSD、DLSSG、FSR、XeSS、Streamline、Reflex、Frame Gen、MFG、HDR、scRGB、HDR10、NVAPI、DXR、VRAM |
| 厂商/平台 | NVIDIA、AMD、Intel、Steam、Epic、GOG、Xbox、Game Pass、Nexus、PCGamingWiki、Discord |
| 渠道/模式值 | Stable、Nightly、Development、Global、Custom、Select、Off、Auto |
| 技术标识 | 文件名、路径、.ini 键名、版本号（v2.0.1）、日志文本、游戏名、存档/插件文件名 |

## 2. 统一译法（必译）

| 英文 | 中文 | 备注 |
|---|---|---|
| Install | 安装 | 按钮 |
| Uninstall | 卸载 | |
| Update | 更新 | 按钮用"更新"，名词用"更新" |
| Update All | 全部更新 | |
| Apply | 应用 | |
| Cancel | 取消 | |
| Close | 关闭 | |
| Browse | 浏览 | |
| Refresh | 刷新 | |
| Rescan | 重新扫描 | |
| Settings | 设置 | |
| Download source | 下载源 | |
| Accelerate downloads | 加速下载 | |
| Mirror | 镜像 | "加速通道"用于说明性长句 |
| Fallback | 回退 | |
| Integrity check | 完整性校验 | |
| Game library | 游戏库 | |
| Detected games | 已检测游戏 | |
| Not installed | 未安装 | |
| Installed | 已安装 | |
| Outdated | 有新版本 | 按钮/标签 |
| Compatibility | 兼容性 | |
| Override | 覆盖 | "per-game override" = 单游戏覆盖 |
| Back up | 备份 | |
| Restore | 还原 | |
| Patch notes | 更新日志 | |
| Troubleshooting | 故障排查 | |
| Framerate | 帧率 | |
| Resolution | 分辨率 | |
| Upscaler | 超分辨率 | |
| Sharpening | 锐化 | |
| Shader | 着色器 | |
| Addon | 插件 | |
| Preset | 预设 | |

## 3. 风格规则

1. **按钮用动词原形**（2~4 字）：安装 / 更新 / 应用 / 还原 / 取消。
2. **标签用名词短语**：帧率上限 / 抗锯齿 / 着色器包。
3. **说明句用陈述语气**，句末不加句号；不用"您"，统一用"你"。
4. **短译优先**：按钮文案 ≤ 4 字；放不下就把完整说明挪到 Tooltip。
5. **平台词统一**："主界面""设置页""详情面板""状态栏""游戏列表"。
6. **不得中英混排造词**（如"安装ReShade插件"要写成"安装 ReShade 插件"，中英文之间留半角空格）。
7. **数字、版本号、百分比保持原样**（2.0.1、100%、4K）。
8. 提示语保持简洁："安装失败，请检查日志" 而不是长段落。

## 4. 待主人拍板的小分歧

1. `Upscaler` → "超分辨率"（小白推荐）还是"升采样"/"图像放大"？
2. `Override` → "覆盖"（推荐）还是"覆写"？
3. `Addon` → "插件"（推荐）还是"附加组件"？
4. `Nightly` 渠道名保留英文（推荐）还是译"每夜构建"？
