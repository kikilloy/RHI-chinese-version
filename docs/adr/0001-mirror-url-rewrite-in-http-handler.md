# 0001 — 把镜像重写放在 HttpClient 的 DelegatingHandler 层

- 状态：已接受
- 日期：本会话
- 相关：`Services/Net/MirrorPolicy.cs`、`Services/Net/MirrorRewriteHandler.cs`

## 背景

程序要从 GitHub 拉取上百种组件，下载 URL 散落在 154 个 service 文件里：`github.com` 129 处、`raw.githubusercontent.com` 16 处、`api.github.com` 33 处、`nightly.link` 5 处。其中一部分 URL 还来自远端数据（`manifest.json` 的 `DownloadUrl`/`DirectUrl`/`releaseApiUrl`），也就是"程序自己都不知道会拿到什么地址"。

需求是"让这些下载可以走镜像站/加速通道"，并且以后新增下载点时不能再漏。

## 考虑过的选项

1. **逐个 service 拼镜像 URL**：控制精细，但要改几十个文件；远端下发的 URL 还得在每处解析；新增下载点必然遗漏。
2. **在 HTTP 管道中间层重写 URL**：一处改动覆盖全部调用点，包括远端数据里来的地址。
3. **用户手动改 hosts / 代理软件**：不落在应用里，做不到"一个按钮"。

## 决定

选 **2**：在共享 `HttpClient` 的管道里加一层 `MirrorRewriteHandler`，位置在已有的 `GitHubTokenHandler` **外层**。

- 分流逻辑抽成纯函数 `MirrorPolicy`，可单元测试，不接触设置存储。
- `api.github.com` 永不重写：条件请求/ETag/限流/摘要查询必须走直连可信通道。
- 只对 `GET`/`HEAD` 做镜像重试（请求体不可重放的方法原样放行）。

## 后果

**好处**：零侵入现有 service；远端下发的地址自动被覆盖；阈值、熔断、日志集中在一处；测试成本低。

**代价**：URL 重写对调用方不可见，排障时必须看 `[Mirror]` 日志前缀，而不是看调用点代码。为此所有重写与回退都强制写日志。

**约束**：因为重写发生在管道层，任何"必须在某某域名上验证"的逻辑（如 token 只发 api.github.com）都必须显式跳过重写，而不是依赖调用方自觉。
