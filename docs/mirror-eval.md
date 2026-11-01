# 镜像实测记录（M1.1）

> 实测时间：2026-04（本次会话）
> 环境：Windows 11 · Git Bash curl 8.19.0 (Schannel) · 无 `http_proxy`/`https_proxy` 环境变量 · 大陆家宽
> 测试资产
> - release 资产：`https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-devkit.addon64`（5,454,336 B = 5.20 MiB）
> - raw 小文件：`https://raw.githubusercontent.com/RankFTW/RHI/main/motd.md`（186 B）
> - raw 大文件：`https://raw.githubusercontent.com/RankFTW/RHI/main/database/pcgw_data.json`（5,306,366 B）
> - archive：`https://github.com/RankFTW/RHI/archive/refs/heads/main.tar.gz`

---

## 1. 结论（先看这里）

**默认镜像顺序（选中 3 个，全部通过内容诚信校验）**

| 顺序 | 前缀 | 判定 | 理由 |
|---|---|---|---|
| 1 | `https://gh.nxnow.top/` | ✅ 首选 | 唯一在 60 s 内完整拉完 5.2 MiB 资产的镜像（32.9 s）；Range 严格（`bytes=0-1023` 正好回 1024 B）；archive/raw/404 全部正确 |
| 2 | `https://ghfile.geekertao.top/` | ✅ 备选 | sha256 一致；速度中等（45 s 拉 2.0 MB）；HEAD/Range/archive/404 均正常 |
| 3 | `https://gh-proxy.com/` | ✅ 末位备选 | 老牌、透传最老实；速度最慢但仍优于直连 |
| — | `https://ghproxy.imciel.com/` | ❌ 淘汰 | archive（codeload）请求失败 `http=000`，源码包场景会漏 |
| — | `https://gh-proxy.org/` | ❌ 淘汰 | 五个可用镜像里最慢（45 s 只 0.41 MB），且 raw 曾用 15.9 s |
| — | `https://cdn.jsdelivr.net/` | ⚠️ 默认关闭 | 可用（内容与 raw 一致），但边缘缓存 `s-maxage=43200`（12 h），manifest 会陈旧最多半天 → 详见 §6 |

**淘汰硬理由**：下面所有 `http=000` 的镜像在判定标准上是"**不老实**或**根本连不上**"，一律不进默认列表。

---

## 2. 连通性（HEAD + GET 1 KB 双探测）

判定用 release 资产 URL 拼在镜像前缀后：`https://{mirror}/https://github.com/...`。

### 2.1 通过

| 镜像 | HEAD | 实际下载 | Content-Length | Accept-Ranges |
|---|---|---|---|---|
| `gh.nxnow.top` | 200 / 1.24 s | ✅ | 5454336（与直连一致） | bytes |
| `gh-proxy.org` | 200 / 1.46 s | ✅ | 5454336 | bytes |
| `ghfile.geekertao.top` | 200 / 2.68 s | ✅ | 5454336 | bytes |
| `ghproxy.imciel.com` | 200 / 6.40 s | ✅ | 5454336 | bytes |
| `gh-proxy.com` | 200 / 2.39 s | ✅ | 5454336 | bytes |

### 2.2 淘汰

| 镜像 | 症状 | 判断 |
|---|---|---|
| `ghfast.top` | HEAD/GET 15 s 超时，DNS 解析到 `107.175.190.197` 但连接无响应 | 不可用 |
| `github.moeyy.xyz`、`gh.llkk.cc`、`mirror.ghproxy.com` | DNS 返回 `2a03:2880:f1xx:83:face:b00c:0:25de`（Facebook IPv6 段） | **DNS 污染/劫持**，物理上不可达 |
| `gh.xxooo.cf`、`ghp.ci`、`gitdl.cn`、`proxy.yaohud.cn`、`hub.whtrys.space`、`ghproxy.cfd`、`gh.6yit.com`、`gh.200303.xyz`、`gh.api.99988866.xyz` | `http=000`，10 ms 内即失败 | DNS 不存在/连接被拒 |
| `hub.gitmirror.com`、`gitproxy.click`、`gh-proxy.net`、`ghproxy.cc` | `http=000` | 域名已失效 |
| `ghproxy.net` | 502 / 15 s 超时 | 服务端故障 |
| `ghproxy.homeboyc.cn` | 403 | 拒绝服务 |
| `github.akams.cn` | 404 | 不支持"前缀 + 完整 URL"形态 |
| `ghp.arslantu.xyz` | 525 | TLS 握手失败 |
| `ghproxy.cxkpro.top`、`gh.ddlc.top` | 429 | 限流，不能作默认 |

---

## 3. 内容诚信（最关键的判定）

方法：直接对比镜像下载物与直连下载物的 **sha256**。不一致 = 篡改或缓存错乱 = **直接淘汰**。

```bash
curl -sL -o direct.md "https://raw.githubusercontent.com/RankFTW/RHI/main/motd.md"
curl -sL -o mirror.md "https://gh.nxnow.top/https://raw.githubusercontent.com/RankFTW/RHI/main/motd.md"
sha256sum direct.md mirror.md   # 必须相同
```

直连基准：`788186855a4b733d32077eb5cc7248a04ecc5b0bfc05b87fad53889eb2e37bbf`

| 镜像 | sha256 | 结论 |
|---|---|---|
| `gh-proxy.com` | `788186855a4b733d…` | ✅ 一致 |
| `ghfile.geekertao.top` | `788186855a4b733d…` | ✅ 一致 |
| `gh.nxnow.top` | `788186855a4b733d…` | ✅ 一致 |
| `gh-proxy.org` | `788186855a4b733d…` | ✅ 一致 |
| `ghproxy.imciel.com` | `788186855a4b733d…` | ✅ 一致 |

**五个存活镜像内容全部原样透传，没有发现投毒行为。**（但这不等于可以省掉 `DownloadIntegrityGuard`：公益镜像可以随时变，校验是唯一长期保障。）

---

## 4. HEAD / Range / archive / 404 行为

| 项 | direct | gh.nxnow.top | ghfile.geekertao.top | gh-proxy.com | ghproxy.imciel.com | gh-proxy.org |
|---|---|---|---|---|---|---|
| HEAD release 资产 | 302 | 200 | 200 | 200 | 200 | 200 |
| Range `0-1023`（release 资产） | — | **206 / 1024 B** | 206 / 1024 B | 206 / 1024 B | 206 / 1024 B | 206 / 1024 B |
| Range `0-1023`（5 MB raw） | 206 | **206 / 1024 B** | 206 / 1990 B | 206 / 1990 B | 206 / 1990 B | 206 / 1990 B |
| archive（codeload） | 302 | 200 / 3.9 MB | 200 / 1.1 MB | 200 / 0.43 MB | **000 失败** | 200 / 0.49 MB |
| 不存在文件 | 404 | 404 | 404 | 404 | 404 | 404 |

要点：
1. **404 全部正确透传**——不会把不存在返回 200 空文件（否则会写坏文件、触发无限更新）。
2. `gh.nxnow.top` 是唯一 Range 严格透传的（1024 B 就是 1024 B）。其余镜像对大文件 Range 会回 1990 B（内部合并 chunk）。→ **下载逻辑不要依赖"Range 回多少字节"**，只用来判断"是否支持续传"，不要用它算偏移校验。
3. `ghproxy.imciel.com` archive 直接连不上 → 淘汰（源码包场景会失败）。
4. **下载 URL 优先拼 `github.com/...`，不要拼 `release-assets.githubusercontent.com` 的签名 URL**：签名带 `se=…` 过期时间，走代理极易过期失败。HttpClient 自己跟随 302 更可靠。

---

## 5. 速度（5.20 MiB release 资产，单次限时）

> 说明：本机直连本身偏慢，绝对值受家宽波动影响；**只看相对倍数**。

| 通道 | 完整下载 | 平均速率 | 相对 |
|---|---|---|---|
| **`gh.nxnow.top`** | **32.9 s 完成 ✅** | **≈166 KB/s** | **1.0×（基准）** |
| `ghfile.geekertao.top` | 45 s 内 2.04 MB | ≈46 KB/s | ~3.6× 慢 |
| `gh-proxy.com` | 45 s 内 1.21 MB | ≈27 KB/s | ~6.2× 慢 |
| `ghproxy.imciel.com` | 45 s 内 0.78 MB | ≈17 KB/s | ~9.8× 慢 |
| `gh-proxy.org` | 45 s 内 0.41 MB | ≈9 KB/s | ~18× 慢 |
| **直连 GitHub** | **180 s 未完成** | ≈30 KB/s | **≈5.5× 慢于首选镜像** |

**直连在 3 分钟内没拉完 5 MiB**——这就是主人要加速通道的直接证据。首选镜像把同样的文件压到 33 s。

---

## 6. jsDelivr 兜底评估（对计划书 Q3 的修正）

| 测试 | 结果 |
|---|---|
| `cdn.jsdelivr.net/gh/jquery/jquery@3.7.1/dist/jquery.min.js` | 200 / 87,533 B（服务本身可用） |
| `cdn.jsdelivr.net/gh/RankFTW/RHI@main/motd.md` | 200 / 186 B，内容一致 |
| `cdn.jsdelivr.net/gh/RankFTW/RHI@main/manifest.json` | 200 / 79,093 B，sha256 与 raw **完全一致** |
| 响应头 | `Cache-Control: public, max-age=604800, s-maxage=43200`（边缘缓存 12 h） |

问题：**`s-maxage=43200` 意味着 manifest 最多可以陈旧 12 小时。** RHI 的 `manifest.json` / `dlss_manifest.json` / `motd.md` 是"推 main 即上线"的活数据，陈旧半天会直接表现为"发版了但用户看不到更新/黑名单没生效"。

**建议（已按此实现）**：
- jsDelivr 仅作为 **raw 的第三顺位兜底**，且**默认关闭**（设置项 `UseJsDelivrFallback`，默认 `false`）。
- 即便开启，也**只用于 `raw.githubusercontent.com` 的小文件**，且需要配 ETag/时间戳判断，不能当成 manifest 的主通道。
- 前缀代理（前三个镜像）已覆盖 raw，jsDelivr 的边际价值低于它带来的"数据陈旧"风险。

---

## 7. 复现命令（下次换镜像时照抄）

```bash
MIRRORS="gh.nxnow.top ghfile.geekertao.top gh-proxy.com"
ASSET="https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-devkit.addon64"
RAW="https://raw.githubusercontent.com/RankFTW/RHI/main/motd.md"

# 1) 连通性
for m in $MIRRORS; do curl -sI -m 15 -o /dev/null -w "$m HEAD=%{http_code} %{time_total}\n" "https://$m/$ASSET"; done

# 2) 内容诚信（必须全绿）
curl -sL -o /tmp/d.md "$RAW"; sha256sum /tmp/d.md
for m in $MIRRORS; do curl -sL -o "/tmp/$m.md" "https://$m/$RAW"; sha256sum "/tmp/$m.md"; done

# 3) Range
for m in $MIRRORS; do curl -s -H "Range: bytes=0-1023" -o /dev/null -w "$m range=%{http_code}/%{size_download}\n" "https://$m/$ASSET"; done

# 4) 速度（只看完成时间与平均速率）
for m in $MIRRORS; do curl -sL -m 60 -o /dev/null -w "$m %{speed_download} B/s %{time_total}s\n" "https://$m/$ASSET"; done
```

---

## 8. 落地到代码

- `MirrorPolicy` 的**硬编码兜底列表**（manifest 拉不到时用）按本文 §1 顺序写死这 3 个。
- `manifest.json` 新增 `"mirrorPrefixes"` 下发同样这 3 个（顺序敏感：先试最快）。
- 熔断策略：同镜像连续失败 2 次 → 本会话 10 分钟内跳过（见计划书 §4.4）。
- 完整性校验默认开启（见计划书 §4.5 与 ADR-0002）。
