# 0003 — 语言包用 JSON，且用英文原文当 key

- 状态：已接受
- 日期：本会话
- 相关：`Localization/Loc.cs`、`Localization/LanguageService.cs`、`Lang/*.json`

## 背景

项目零 i18n 基础：没有 `.resw`、没有 `ResourceManager`，界面由 C# 代码构建（`DetailPanelBuilder.*`、`SettingsHandler.*`、`DialogService.*`）。去重候选串约 1847 条，真实 UI 文案估 1100~1600 条。首个目标语言是简体中文。

## 考虑过的选项（存储）

1. WinUI 原生 `.resw` + `ResourceLoader`/`x:Uid`：VS 生态友好，但 `x:Uid` 依赖 XAML（本项目 UI 不走 XAML），社区贡献翻译不便，热加载麻烦。
2. 自建 JSON 语言包 + `Loc.T(...)`：随程序分发，缺键可回退，社区只改 JSON 即可贡献。

## 考虑过的选项（键的形式）

1. 语义键（`settings.download.mode`）：改名自由，但要给上千条串造键，抽取成本高。
2. **英文原文当键**（`"Download source": "下载源"`）：抽取成本≈0，缺键天然回退原文，翻译表人可读。

## 决定

JSON 语言包 + **英文原文作键**。基线是 `Lang/en.json`（英文，用于失配检测），产物是 `Lang/zh-Hans.json`。

## 后果

**好处**：改代码只是"把字面量包进 `Loc.T()`"；缺键不会出现空白；主人抽查翻译时看到的是 `"Installed": "已安装"`，一眼可判；以后加日语/俄语只需新增一个 JSON。

**代价**：英文原文一改，该条翻译失配并回退英文。用 `validate.py` 对比基线列出失配键，重译那几条即可。

**边界**：固有名（组件名、渠道名）、日志文本、文件名与路径、版本号、游戏名**不进语言包**——它们不是给人翻译的，是给人查的。
