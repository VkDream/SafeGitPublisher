# SafeGitPublisher（GitHub 安全发布助手）V1

SafeGitPublisher 是 Windows 桌面版 "GitHub 安全发布助手"，用于在把项目推送到 GitHub 之前自动执行发布前安全检查（Secret 扫描、敏感文件、大文件、作者身份、Remote、图片脱敏、构建验证），并把 "检查 → 提交 → 推送" 收敛为一次受保护的发布流程。

技术栈：C# / WPF / .NET 10 / x64 / MVVM。

---

## 一、安全 Gate（发布前检查，按固定顺序执行）

| # | 检查项 | 状态含义 | 阻断范围 |
|---|--------|----------|----------|
| 1 | Git 环境 | 未安装 git → Blocked | 提交 + Push |
| 2 | 仓库检测 | 非仓库 → Blocked（可一键 git init） | 提交 + Push |
| 3 | 工作区状态 | 合并冲突（AA/UU/DD/AU/UA/DU/UD）→ Blocked | 提交 + Push |
| 4 | .gitignore | 缺失推荐规则 → Warning（可一键生成，只追加不覆盖） | 不阻断 |
| 5 | 敏感文件 | bin/obj/publish/tmp/.vs/.claude、*.db、.env、secrets.json、appsettings.Local.json、*.pfx/*.key/*.pem、*.log 等 → Blocked；已被 .gitignore 排除的显示"已安全忽略" | 提交 + Push |
| 6 | Secret 扫描 | github_pat_/ghp_/sk-/AKIA/Bearer → Blocked；赋值型 secret/password 明文（High）同样硬阻断；内网 IP/非本机 Server → Warning；关键字 → Info | 提交 + Push |
| 7 | 大文件 | >10MB Warning、>50MB 高危 Warning、>100MB（GitHub 硬限制）→ Blocked | 提交 + Push |
| 7.5 | **仓库总体积**（V1.0.1 新增） | 待提交合计 >500MB → Warning、>1000MB → Blocked（阈值可在设置调整）；详情按扩展名汇总 Top 占用。动机：单文件阈值拦不住"113 张 14MB 位图共 1.6GB"（2026-08-13 ReadCode 真实现场） | 提交 + Push |
| 8 | Git 作者 | 与推荐身份不一致 → Warning（一键应用，只写仓库 local 配置） | 不阻断 |
| 9 | Remote | 未配置 origin → Warning（禁 Push，可设置）；地址畸形（https\:// 等）→ Blocked | Push |
| 10 | 分支 | master → Info 提示；detached HEAD / 读取失败 → Blocked | detached HEAD / 读取失败禁 Push |
| 11 | 图片脱敏 | 本次含新增/修改图片 → Warning 并禁 Push，需勾选"图片已完成脱敏检查" | Push |
| 12 | 构建 | .NET 项目 build 失败 → Blocked（禁 Push）；构建警告 → Warning | Push |

规则：
- **BLOCKED = 禁 Push**（部分检查同时禁提交）；**WARNING = 复核后确认可继续**；High 明文凭据按 Blocked 处理。
- 提交流程最终闸门：`git add --all` 后从 index Git blob 原始字节复扫；中止时恢复操作前 index tree，不清空用户原有部分暂存。
- 已扫描 index tree 与实际 commit tree 必须一致；hook 在扫描后改写暂存内容会禁止成功回包与 Push。Push 前还会扫描真实待推送历史。
- 危险命令黑名单（force push / rebase / reset --hard / clean -fd / filter-repo / branch -D / restore / checkout -- . 等）**绝不自动执行**。
- “安全提交并上传”会在任何暂存/提交前先用 git.exe 探测精确 origin 的远端分支；网络或认证路径不可用时不会先制造本地提交。真正 Push 固定使用已锁定的完整提交 OID：`<full-oid>:refs/heads/<branch>`；同步远端仅使用 `git pull --ff-only`，从不自动 merge。
- 所有 Secret 输出统一脱敏（保留公开前缀 + **** + 末尾 4 位），日志与界面永不出现凭据原文。

## 二、界面功能

- 最近项目（10 个）+ 选择项目 → 自动检查
- 13 项检查结果列表（✅/⚠/🚫 + 颜色）+ 一键修复按钮
- 变更文件列表（状态/大小/风险）与详细报告对话框
- 提交类型中文显示（新增功能/问题修复/文档更新/代码重构/日常维护/测试调整），内部仍使用 feat:/fix:/docs:/refactor:/chore:/test:
- 两个常规发布按钮：**仅提交** / **安全提交并上传**，均带最终确认页
- 独立恢复入口：**检查并上传已有提交**。用于“本地提交已成功，但网络中断导致尚未 Push”的现场；它只核对并上传锁定的已有提交，不会再次暂存或重复 commit
- 首次发布向导：git init → .gitignore → 作者身份 → 设置 origin（勾选时）→ 完整检查 → 最终确认 → 发布
- 设置对话框（大文件阈值、仓库总体积阈值、构建开关、图片确认开关、推荐作者）

## 三、目录结构

```
E:\SafeGitPublisher
├─ SafeGitPublisher.slnx
├─ src\SafeGitPublisher            # WPF 主工程（net10.0-windows / x64 / WinExe）
│  ├─ Models\                      # 领域模型（报告、变更、设置等）
│  ├─ Services\                    # 进程/ Git / 扫描 / 检查 / 发布服务
│  ├─ ViewModels\                  # MVVM
│  └─ Views\                       # 主窗口 + 对话框（XAML 可视化界面）
├─ tests\SafeGitPublisher.Tests    # 零依赖控制台单测（当前 156 项）
└─ tests\SafeGitPublisher.E2E      # 真实 git 临时仓库端到端测试（34 项）
```

## 四、构建与测试

```powershell
# 构建（Release）
dotnet build "E:\SafeGitPublisher\SafeGitPublisher.slnx" -c Release

# 单元测试
dotnet run --project "E:\SafeGitPublisher\tests\SafeGitPublisher.Tests\SafeGitPublisher.Tests.csproj" -c Release

# 端到端测试（只使用 %TEMP% 下的临时仓库，不接触真实仓库）
dotnet run --project "E:\SafeGitPublisher\tests\SafeGitPublisher.E2E\SafeGitPublisher.E2E.csproj" -c Release

# 启动
dotnet run --project "E:\SafeGitPublisher\src\SafeGitPublisher\SafeGitPublisher.csproj" -c Release
# 或直接运行：
# E:\SafeGitPublisher\src\SafeGitPublisher\bin\Release\net10.0-windows\SafeGitPublisher.exe
```

## 五、设置存储

`%LOCALAPPDATA%\SafeGitPublisher\settings.json`（不写入程序目录）。

## 六、当前验证结果（2026-08-13，V1.0.1 安全加固 + 仓库总体积门禁）

- **本轮（2026-08-13 下午，仓库总体积门禁）**：
  - 单元测试：156/156 通过（465 assertions；新增 REPOSIZE-01~06：总量分类阈值/求和跳过删除项/扩展名 Top 汇总/**真实现场复现（113 张 14.42MB 位图共 1.59GB 必须阻断，排除后纯源码放行）**/设置默认值序列化合同/Gate 文案映射）
  - E2E：新增 RS01~03 全过（总量阻断+commit 前最终门禁同合同拦截 / 警告级不阻断 / Pass+检查项≥13 合同）
  - Debug/Release 构建：0 Warning / 0 Error；GUI 冒烟（含新增"仓库总体积规则"设置组）通过
  - **新功能：仓库总体积门禁（第 13 项检查 repo_size）**：待提交合计 >500MB → Warning、>1000MB → Blocked（同时阻断提交与推送，阈值设置可调，0 < 警告 < 阻断）；三层同合同——预检（PreflightService）→ commit 前最终门禁（QuickSafetyCheckAsync）→ push 前历史门禁（ScanOutgoingHistoryAsync，按去重 blob 求和）；详情按扩展名汇总 Top 占用（如 `.bmp ×113 = 1590.5 MB`）。动机：2026-08-13 ReadCode 真实现场——805 个变更共 2.05GB，113 张 14.42MB 位图每张单独仅 Warning，单文件阈值全部放行。
  - **E2E 重跑发现并修复安全加固轮（2026-08-12/13，该轮未跑 E2E）回归 4 项**：
    - T03（真实产品 bug）：显式 URL push（安全设计）不创建 `refs/remotes/origin/<branch>` 跟踪引用 → 随后 `set-upstream-to` 在 Git ≥2.37 报 `the requested upstream branch 'origin/main' does not exist`（`advice.setUpstreamFailure`），用户看到"Push 成功但 upstream 失败"假警报。修复：push 核验成功（远端 OID 与锁定提交一致）后，先 `git update-ref refs/remotes/origin/<branch> <已核验OID>` 纯本地精确创建跟踪引用（`GitService.SetOriginTrackingRefAsync`，无网络），再 set-upstream。
    - T05/T09/T13（测试断言过时）：安全加固把 origin 网络探测提到安全扫描之前，老用例 CommitAndPush 无 origin → 在安全门前被远端门禁拦截（Committed=False 行为正确），断言文案不命中。修复：这三个用例改 CommitOnly（同样经过 QuickSafetyCheck 安全门，验证意图不变，已加注释说明合同）。
    - 修复后 E2E：**34/34 全过**。
- 纯单元/静态合同测试：149/149 通过（442 assertions）；本轮显式排除 `GuiSmokeHost`、`DialogSmokeTests`、`GuiStartupSmoke`，保证不启动 GUI 或 Git 进程；包含安全测试源码自扫描与"已有提交仅上传"防复发合同。（2026-08-13 上午安全加固轮记录）
- 历史 E2E 基线：31/31（2026-08-08）；2026-08-13 安全加固轮因未获得 Git 调用授权未重跑；本轮（同日下午）重跑并修复安全加固轮回归后：**34/34 全过**。
- Debug 构建：0 Warning / 0 Error
- Release 构建：0 Warning / 0 Error；ProductVersion=1.0.1 / FileVersion=1.0.1.0
- XAML XML 静态解析通过；本轮未启动 GUI，中文前缀下拉框、最终确认页图片勾选仍待用户目视确认。
- **Push 网络中断恢复（2026-08-13）**：常规提交并上传新增 commit 前远端探测；已产生本地提交但 Push 未开始/结果未知时，主界面提供“检查并上传已有提交”。恢复流程锁定完整 OID、分支和脱敏 Remote 指纹，重新核对远端状态及安全 Gate，只上传既有提交，绝不再次 add/commit；取消、超时或远端结果无法确认时保持 Unknown 并要求先核对，禁止盲目重复 Push。
- **V1.0.1 external dogfooding（首个外部真实验收：DeepSeekBalanceTray）**：
  - SGP-UI-001：`.gitignore 预览`按钮不可见 → 根因：构造函数 `Content = data.NewContent` 把 Window.Content（含按钮的整个 XAML 根 Grid）整体替换为纯文本。修复：改写入 `ContentBox.Text`；布局加固（内容区 * 行 MinHeight=200、按钮行 Auto MinHeight=44、窗口 MinWidth/MinHeight=620/460、内容区可滚动、按钮加 x:Name）。取消/应用保持 isReadOnly 只追加合同。
  - SGP-UI-002：`PUBLISH BLOCKED` 显示"存在 0 项阻断问题"根因：Detail 用 `report.BlockedCount`，而 Banner 变红可能由 Warning 级但 BlocksPush=true（未配置 origin）触发。修复：新增 `PublishBannerEvaluator.BlockedDetail()`——真实 Blocked=N → "存在 N 项阻断问题"；无 Blocked 但 Push 硬拦截 → "存在 N 项需处理问题，当前无法发布"，绝不显示"0 项"。ReviewRequired 文案同步进纯函数。
  - 外部真实验收：E:\软件部\开发项目 自测\DeepSeekBalanceTray（VkDream/DeepSeekBalanceTray.git，main，22 变更）：Secret/Sensitive/Large/Identity/Remote/Branch 全 PASS；DeepSeek API Key 从环境变量读取（`DEEPSEEK_BALANCE_SERVICE_API_KEY`/`DEEPSEEK_API_KEY`），无硬编码、不进待提交内容；隔离构建 DeepSeekBalanceTray.sln PASS（ExitCode=0）。仅 .gitignore 缺 22 条推荐规则 → Warning（GUI"补充推荐规则"后 PASS）。达到 READY TO PUBLISH 后由用户自行 commit+push。
- **真实自发布（SELF-PUBLISH 闭环，V1.0.0）**：用户通过 SafeGitPublisher 自身 GUI 对 E:\SafeGitPublisher 执行真实 commit + push，成功推送到 GitHub
  - Repository：`VkDream/SafeGitPublisher`；Branch：`main`
  - Commit：`eee477742188c62c1d5c6e51f4c76b9a0cfeac69`；Message：`release: SafeGitPublisher v1.0.0`
  - Push 状态：**成功**（GitHub 远端已接收）
- 真实只读自检查 E:\SafeGitPublisher（PreflightService 全链路，含真实 dotnet build）：Git 仓库/作者/Remote(origin → VkDream/SafeGitPublisher)/分支 main/Build PASS，Build Target=SafeGitPublisher.slnx；生成推荐 .gitignore 后 bin/obj 不再进变更，敏感文件 PASS、Secret Scan PASS
- 封版回归修正（V1.0.0）：真实自发布后仓库出现首个 HEAD commit，.NET SDK 默认向 InformationalVersion 追加 commit hash（1.0.0+eee4777...）导致版本精确性断言失败 → csproj 增加 `IncludeSourceRevisionInInformationalVersion=false` 修复（版本仍为 1.0.0，未升级）；修复后 Debug/Release 0W/0E、单测 87/87、E2E 22/22 全量通过
- **self-host 缺陷修复（V1.0.0 封版后真实现场）：0 变更假 PUBLISH BLOCKED**：成功 commit+push 后工作区归零，post-publish 刷新预检仍执行 `dotnet build`（现场该构建失败）→ 误显示 PUBLISH BLOCKED。修复合同：0 个可提交变更 → Build Gate 不执行（Not Required，SkipReason 明确），报告 Info 不阻断；存在可提交变更 → Build Gate 原样强制（失败仍阻断，安全未削弱）；Banner 0 变更 → UP TO DATE，仓库级致命异常（非 Git 仓库/git 不可用/合并冲突）即使 0 变更仍 BLOCKED。新增 `Services\PublishBannerEvaluator.cs`（纯函数）；构建失败日志增强 ExitCode + 关键错误前 3 行。回归：单测 99/99（214 assertions）、E2E 24/24（Z07/Z08）
- **self-host 缺陷修复 2（V1.0.0 Tag 前）：Self-Build 隔离输出**：SafeGitPublisher.exe 自身运行中 + 有真实变更 → 传统 build 输出触碰运行中的自身 EXE → MSB3027/MSB3021（已自动化复现）。修复：**发布前 .NET 构建使用隔离临时输出（`dotnet build --artifacts-path %TEMP%\SafeGitPublisher\PreflightBuild\<GUID>`），不覆盖项目正在使用的本地运行输出**；Build Gate 仍为真实强制 Gate（编译错误仍阻断，隔离不是假构建）；`.serena/` 纳入本机 AI/开发工具元数据 ignore/sensitive 策略（与 .claude/ .reasonix/ 同级）。回归：单测 108/108（238 assertions）、E2E 26/26（S01/S02）
> **Build Target Resolution（V1.0.0 修复）**：不再假定 csproj 位于仓库根目录。解析规则（`Services\BuildTargetResolver.cs`）：
> 根目录唯一 `*.sln`/`*.slnx` → 构建该 solution（支持 .slnx）｜多 solution → 与仓库名匹配优先，否则判定歧义｜无 solution → 递归搜 csproj｜唯一 csproj → 构建（子目录亦可）｜多 csproj → 仓库名匹配主应用优先，否则判定歧义｜完全无 .NET 项目 → 跳过构建（Info，不报 MSB1009）。开启构建门禁时，歧义会禁止 Push，需先整理出明确目标再重查。
> 报告显示 Build Target 文件名 + 命令摘要（如 `dotnet build SafeGitPublisher.slnx`）；失败时展示 Target / Exit Code / 关键错误摘要。

> **SGP-UI-002（V1.0.1）**：PUBLISH BLOCKED 的 Detail 不再展示"存在 0 项阻断问题"。安全语义与显示语义分离：真实 Blocked=N → "存在 N 项阻断问题"；无 Blocked 但存在 Warning 且 BlocksPush=true（如未配置 origin）→ "存在 N 项需处理问题，当前无法发布"。ReviewRequired 文案同步进 `PublishBannerEvaluator` 纯函数（`BlockedDetail` / `ReviewRequiredDetail`）。

- 静态检查：全部 XAML 走可视化设计、无硬编码版本（统一 AppVersionService 程序集元数据）、对话框空列表均有空状态
- Secret 扫描范围：预检扫描真正可提交的工作区候选；最终 Gate 会对待提交 index 与待推送历史中不超过 100MiB 的 Git blob 读取原始字节并做内容/编码探测，不按 `.png`、`.dll` 等扩展名直接信任跳过。已忽略文件和删除项不参与工作区文件读取；无真实 Secret 时 PASS（测试种子运行时拼接，源码内不含完整凭据模式）。

> **Zero Change Gate（V1.0.0 新增，人工验收 Bug 修复）**：0 个可提交变更时，无论提交说明是否填写（含 "test:"），CanCommit=CanPush=false，确认页不会打开。
> 双层防护：① ViewModel/UI 层 CanExecute 实时响应变更数/提交说明/检查结果/忙碌/图片确认（Tooltip 说明禁用原因）；② WorkflowService 打开确认页前重新读取真实 `git status`，0 变更即中止（INFO 提示，非错误红叉），路径集合与最近检查不一致时要求重新检查。

## 七、已知限制（V1）

- 网络 Remote 只允许 GitHub（HTTPS / `git@` / `ssh://git@`）；非 GitHub host、明文 HTTP、嵌入凭据或异常 SSH 用户会阻断。受控本地路径 Remote 仅用于本地验证。
- 图片脱敏确认为人工确认制（工具不进行图像内容识别）。
- 构建检查只支持 .NET 项目（识别 csproj/sln）；其他语言项目显示"跳过构建"。
- 危险命令（force push、rebase、reset --hard 等）不提供自动化执行入口。
- 不管理 Token 凭据、不调用 GitHub REST API（Push 由 git.exe 完成，是否输入账号密码由 git 自身凭据管理器处理）。
- 浏览器能打开 GitHub 不代表 git.exe 使用相同的代理、VPN 或网络路径；本工具会安全阻断并保留恢复入口，但不会擅自修改系统/Git 代理或网络配置。
