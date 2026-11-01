# i18n 管线（Hy-MT2 + llama.cpp）

> 状态：W1（外壳）已交付。W2 / W3 待做。
> 相关：`docs/i18n-glossary.md`（术语）、`docs/adr/0003-json-language-pack-english-as-key.md`（为什么用 JSON + 英文原文当 key）

## 1. 一句话

`extract.py` 抽候选 → `translate.py` 调本地模型 → `validate.py` 机器校验 → `wire.py`/手工包 `Loc.T` → `publish.bat` 出包走查。

语言包是 `RenoDXCommander/Lang/zh-Hans.json`，扁平 `{"英文原文": "中文"}`，随 exe 分发（`<exe>/Lang/`）。
**缺键自动回退英文**，所以任何一波翻译都能独立交付，界面永远不会空白。

## 2. 环境

| 项 | 值 |
|---|---|
| llama.cpp | `E:\ai\llamacpp\llama-server.exe`（b10356，STQ kernel 可用） |
| 模型 | `E:\ai\llm\Hy-MT2-7B\Hy-MT2-7B-Q4_K_M.gguf`（4.6 GB） |
| Python | `E:\python\python.exe`（只用标准库，**不要装包**） |
| GPU | RTX 5060 Laptop 8 GB |

### 启动服务

`~powershell
E:\ai\llamacpp\llama-server.exe -m E:\ai\llm\Hy-MT2-7B\Hy-MT2-7B-Q4_K_M.gguf ^
  --host 127.0.0.1 --port 8099 --jinja -ngl 99 -c 2048 --parallel 1 ^
  --temp 0.7 --top-p 0.6 --top-k 20 --repeat-penalty 1.05 -n 384 --no-warmup
`~

**实测显存**（`nvidia-smi`）：`--parallel 1 -c 2048` = 5795 MiB 总占用（其中桌面 862 MiB，权重约 4.7 GB）。
`--parallel 2 -c 4096` = 6059 MiB。8 GB 卡上两者都不 OOM，但要给桌面留余量。
Smoke test 记录：b10356 + 这个 GGUF **不需要**换构建，输出正常、不重复、不吐乱码。

**翻完立刻关进程**：`Get-Process llama-server | Stop-Process -Force`。

## 3. 四个脚本

### `tools/i18n/extract.py` — 抽取

`~bash
E:/python/python.exe tools/i18n/extract.py --report
`~

扫 `RenoDXCommander/**/*.cs` 与 `*.xaml`，产出 `worklist.json` + `worklist.md`。

**只扫 UI 构建文件**（`WAVE_RULES`），Services / ViewModels / Models 一律不进：那里的字符串是 ini 键、pack id、日志片段，翻了会坏。

**波次**（同一句话在多个波次出现时归**最早**那个，否则同一句只在后面的波次被翻、外壳里仍是英文——"Settings" 就踩过这个坑）：

| 波 | 范围 |
|---|---|
| W1 外壳 | `MainWindow.xaml`、`SettingsHandler.*`、`MainWindow.StatusBar/Events.Settings/Skeleton` |
| W2 流程 | `DialogService.*`、`MainWindow.Events.*`（除 Components）、`MainViewModel.Install/Update/Activity`、`DownloadProgress`、`GitHubApiDiagnostics` |
| W3 详情面板 | `DetailPanelBuilder.*` |
| P1 后续 | 组件 ⚙ 窗口（`MainWindow.Events.Components.*`）、`*PopupHelper`、`CogDialog`、FAQ |

**丢掉的东西**（都写进脚本里的规则）：日志、URL、Windows 路径、文件名、版本号、`%s` 格式符、settings.json 键、命令行参数（`/Query /TN`、`-applaunch`）、方括号开头的调试格式串、CamelCase 资源键（`TitleBarButtonPressedBackground`）、白名单固有名。

### `tools/i18n/translate.py` — 翻译

`~bash
E:/python/python.exe tools/i18n/translate.py --wave W1
E:/python/python.exe tools/i18n/translate.py --wave W1 --limit 20      # smoke test
E:/python/python.exe tools/i18n/translate.py --wave ALL --max-minutes 18
`~

- **一条串一次请求**，不批量（批量会让模型合并/漏译，且单条失败可单条重试）。
- **目标文件就是断点**：每 20 条原子重写 `zh-Hans.json`（`.tmp` + `os.replace`），中断最多丢 20 条。
- **占位符逐条校验**：`{0}` 少一个/多一个 → 降温重试 2 次 → 仍不对就留英文（绝不把坏的 Format 串写进包）。
- 术语只在源串里真出现时才注入（`GLOSSARY` 表），短串保持短、请求快。
- 输出风格在 `apply_style_rules()` 里统一：您→你、中英文之间补半角空格、英文原文没句号就不加句号。

**实测速度**：4.6 条/秒（短串），0.3–0.6 s/条。W1 的 497 条 ≈ 2 分钟。

### `tools/i18n/validate.py` — 校验（替代人工审校）

`~bash
E:/python/python.exe tools/i18n/validate.py --wave W1
`~

**硬失败**（必须为 0 才能接线）：JSON 非法、占位符不一致、模型指令文本泄漏进译文。
**告警**：连续 3 个英文单词、译文长度 > 原文 1.8 倍、同一中文对应两个英文 key、英文句点结尾、完全没有中文。
同时重写 `Lang/en.json`（英文基线）。

> 注意：`extract.py` 会跳过已经包了 `Loc.T` 的行，所以随着接线推进 worklist 会缩小、`en.json` 基线也会缩小。这是预期行为（那些条目已经交付）。validate 报告的 "extra" 段落不是错误。

### `tools/i18n/wire.py` — 一次性接线助手（W1 用过）

`~bash
E:/python/python.exe tools/i18n/wire.py --wave W1 --dry-run
E:/python/python.exe tools/i18n/wire.py --wave W1
`~

按 worklist 的 `file:line + 原始字面量` 精确替换成 `Loc.T(...)`，插值串转成 `Loc.T("...{0}...", expr)`。

**它拒绝碰的**（改完必须编译验证）：`case` 标签、`const`、`Tag/Name/Key` 赋值、比较调用、`TaskName`/`SectionKey`、嵌套字符串的复杂插值（`{(n == 1 ? "" : "s")}`）。

## 4. 接线规则（踩过坑，务必遵守）

1. **XAML 不逐个改**：`MainWindow.xaml` 的静态文案由 `Localization/Loc.VisualTree.cs` 在 `InitializeComponent()` 之后遍历替换 —— 只替换**语言包里有的**文本，所以游戏名/版本号不可能被误伤，也不需要新增 `x:Name`（项目禁令）。
2. **比较值不能包 `Loc.T`**：`last == Loc.T("Just now")` 永远不成立（左边是英文）。只包**显示**的那一段。
3. **`const string` 不能包**：`Loc.T` 不是编译期常量。`DownloadSectionKey`、`AdminTaskName = "RHI Admin Mode"` 都属于机器标识，本就不该翻。
4. **复杂插值留人工**：`key {expr ? "" : "s"}.` 这类先拆成完整句子再包，否则包了半截会破坏语法。
5. **多行拼接串**（`"a" +` 换行 `"b"`）在 worklist 里只有第一段，接线时要么整段合成一个 key，要么留着英文。
6. **switch 表达式的分支标签不能包**：`"ReShade" => (bg, fg)` 里的字符串是**常量模式**，包了直接编译不过。`wire.py` 已拒绝"行首是引号"的行。
7. **正则模式 / 注册表参数 / 命令行开关绝不能包**：`Regex.IsMatch(hint, @"Unreal Engine \d")`、`reg.exe add ... /v X /t REG_DWORD /f`、`7z x "{0}" -o"{1}" -y` —— 翻了会静默坏功能。extract 现在按 `\d` 形式、`/v` 开关、`HKLM|REG_DWORD` 排除。
8. **XAML 实体要反转义再当 key**：`Text="Accounts &amp; Data"` 在界面上是 `Accounts & Data`，语言包必须用后者做键，否则永远匹配不上（而 `&` 又容易被"code-like"规则误杀）。
9. 数字/浮点解析**保持 InvariantCulture**；`Loc.T` 的格式化也用 InvariantCulture，译文不可能改变数字写法。

## 5. 每波的固定流程

1. `extract.py`（确认本波条数在预算内）
2. 起 llama-server（smoke test 一条，看有没有乱码/复读）
3. `translate.py --wave Wx`（≤ 18 min）
4. 关服务
5. `validate.py --wave Wx` → **硬失败必须 0**
6. 接线（`wire.py` 或手工）→ `dotnet build` 0 错误 → `dotnet test` 262 全绿
7. `publish.bat` → 启动 RHI 读日志（`[Loc.Load] Loaded N strings`、`[LocVisualTree.Apply] Translated N`）+ UI Automation 读界面真实文本
8. 更新 `RHI_PatchNotes.md`、本文件、`docs/handover-cn-acceleration-i18n.md`
9. **问主人能不能提交**

## 6. 实测记录

| 项 | 值 |
|---|---|
| 模型兼容性 | b10356 + Hy-MT2-7B-Q4_K_M：正常，无需换构建 |
| 速度 | 4.6 条/秒（短句），长句 0.5–0.8 s |
| 显存 | 5795 MiB 总占用（parallel 1, ctx 2048） |
| W1 抽取 | 322 条（unique），其中 XAML 204 → 遍历替换 254 处 |
| W1 翻译 | 556 条语言包，0 失败 |
| W1 校验 | 硬失败 0 |
| 覆盖验证 | UI Automation 读到「设置 / 已安装 / 收藏夹 / 其他 / 快速开始 / 更新全部」等中文 |
