# SafeGitPublisher CHANGELOG

## v1.0.1（2026-08-13 下午，仓库总体积门禁）

- **新功能：仓库总体积门禁（第 13 项检查 `repo_size`）**：待提交合计 >500MB → Warning、>1000MB → Blocked（同时阻断提交与推送；阈值可在设置调整，校验 0 < 警告 < 阻断）。三层同合同：预检（PreflightService）→ commit 前最终门禁（QuickSafetyCheckAsync）→ push 前历史门禁（ScanOutgoingHistoryAsync，按去重 blob 求和）。报告详情按扩展名汇总 Top 占用。动机：ReadCode 真实现场 805 变更共 2.05GB（113 张 14.42MB 位图每张单独仅 Warning），单文件阈值全部放行。
- 设置对话框新增"仓库总体积规则（MB）"组（警告/阻断阈值 + 验证）。
- 验证：Debug/Release 0 Warning / 0 Error；单测 156/156（465 assertions，含 GUI 冒烟）；E2E 新增 RS01~03 全过。
- **E2E 重跑暴露安全加固轮（2026-08-12/13，该轮未跑 E2E）回归 4 项，本轮已全部修复**：
  - T03（真实产品 bug）：显式 URL push 不创建 `refs/remotes/origin/<branch>` 跟踪引用 → set-upstream 在 Git ≥2.37 报 `advice.setUpstreamFailure`。修复：push 核验成功后 `git update-ref refs/remotes/origin/<branch> <已核验OID>` 纯本地精确创建跟踪引用（新增 `GitService.SetOriginTrackingRefAsync`，无网络），再 set-upstream。
  - T05/T09/T13（测试断言过时）：origin 前置门禁先于安全门，老用例改 CommitOnly 恢复验证意图（Committed=False 行为本就正确）。
- 修复后全量回归：单测 156/156、**E2E 34/34**、Debug/Release 0 Warning / 0 Error。

## v1.0.1（2026-08-12，安全加固，待 GUI/真实 Git 验收）

- 对抗审查修复：预检快照新鲜度、全局操作租约、图片确认指纹、设置/同步后 Gate 失效、首次发布向导可达性与流程自重入。
- 发布链安全：精确 origin push URL/branch/ref 两次校验；detached HEAD 在提交前阻断；commit 任何失败都失败关闭；index tree 快照精确恢复；已扫描 tree 与实际 commit tree 绑定，拦截 hook 扫描后改写；Push 前扫描真实待推送历史。
- 扫描安全：Secret 支持 UTF-8/GB18030/UTF-16/UTF-32 BOM、>2MiB 文本与无扩展名；读取 index/outgoing Git blob 原始字节；不按 `.png/.dll` 扩展名盲信跳过；扫描/临时文件清理不完整均失败关闭；明文 High 凭据统一硬阻断；>100MiB 始终硬阻断。
- UI：提交类型中文显示但内部保留 Conventional Commits 前缀；最终确认页可完成当前图片指纹的脱敏确认；确认页显示脱敏 Push URL；Git 错误输出进入 UI/日志前脱敏。
- 自扫描修复（2026-08-13）：对抗测试中的 GitHub Token 样本改为运行时分段生成，源码不再保存完整凭据或敏感赋值形态；发布说明中的内网地址/凭据示例改为抽象描述；扫描规则未放宽，断言收紧为必须命中 `ghp / Blocked`；新增安全测试源码与发布说明自扫描防复发用例。
- Push 网络中断恢复（2026-08-13）：常规“安全提交并上传”在 add/commit 前先探测精确 origin；失败时不再先制造本地提交。新增独立“检查并上传已有提交”，绑定完整 OID、分支、Remote 指纹、Build 证明和图片确认，只上传既有提交，不重复 add/commit；Push 启动后的取消、超时或核验失败统一标记为 Unknown，恢复前必须先 reconcile 远端状态。
- 验证：Debug/Release 均 0 Warning / 0 Error；不启动 GUI/Git/网络的纯测试 149/149（442 assertions）；XAML XML 解析通过。本轮未运行 E2E、GUI 冒烟、真实 Git 提交/Push，未创建 Tag/Release。

## v1.0.0（2026-08-07，已真实自发布到 GitHub）

### 里程碑：真实自发布（SELF-PUBLISH 闭环）
- 用户通过 SafeGitPublisher 自身 GUI 对 **E:\SafeGitPublisher** 执行真实 commit + push，成功推送到 GitHub：
  - Repository：`VkDream/SafeGitPublisher`
  - Branch：`main`
  - Commit：`eee477742188c62c1d5c6e51f4c76b9a0cfeac69`
  - Message：`release: SafeGitPublisher v1.0.0`
  - 状态：**Push 成功**，证明 SafeGitPublisher 已完成真实 self-host / self-publish 闭环。
- 该次真实发布同时验证了全部安全 Gate（含 Build Target 解析、Secret 扫描、Sensitive 扫描）在真实仓库下的放行与清理流程正常。

### 修复
- **InformationalVersion commit hash 回归（真实自发布后暴露）**：仓库出现首个 HEAD commit 后，.NET SDK 默认在构建时向 `InformationalVersion` 追加 commit hash（`1.0.0+eee4777...`），导致版本精确性断言（必须恰好 `1.0.0`）失败。修复：csproj 增加 `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`，InformationalVersion 恒为 `1.0.0`（版本值未升级，仍为 1.0.0）。
- **Build Target Resolution 缺陷（V1.0.0 RC 自发布验收发现）**：旧实现假定 csproj 位于 `RepositoryRoot\RepositoryName.csproj`，真实"根 .slnx + src/tests 子目录"结构自发布报 `MSB1009 项目文件不存在`。
  - 新增 `Services\BuildTargetResolver.cs` 纯静态解析器，合同：根目录唯一 `*.sln`/`*.slnx` → 选中；多 solution → 与仓库名匹配优先，否则歧义（需人工选择，不随意猜）；无 solution → 递归扫 csproj（排除 bin/obj/.git/node_modules，深度 ≤8）；唯一 csproj → 选中；多 csproj → 仓库名匹配主应用优先，否则歧义；完全无 .NET 项目 → 跳过构建（非 Build Failed）。
  - **支持 `.slnx`**（与 .sln 同优先级）。
  - `DotNetBuildService` 改为 `dotnet build <解析出的完整路径>`（消除 MSB1009，绝不再传目录名+WorkDir 组合），`IsDotNetProject` 复用解析器。
  - Build UI：报告显示 `Build Target`（文件名，如 `SafeGitPublisher.slnx`）+ 命令摘要（`dotnet build SafeGitPublisher.slnx`）；失败时显示 Target / Exit Code / 关键错误行摘要（≤3 行）；歧义 → Warning（需人工选择）不阻断提交；非 .NET → Info。
- **Secret 扫描自指/种子误报**：扫描器曾把自身规则说明、敏感键赋值示例、凭据格式样例及内网地址样例识别为风险，导致含测试工程的真实仓库无法通过 Secret Scan。修复：文档不再保存可触发规则的完整样例；规则说明与测试种子使用抽象描述或运行时拼接（源码文本不含完整模式，运行断言行为不变）。
- **首次发布 .gitignore 回归验证**：生成推荐 .gitignore 后 `bin/obj`、`*.dll/*.pdb/*.deps.json/*.runtimeconfig*` 不再进入"本次变更"，不再计入 Sensitive BLOCKED；Secret Scan 不再扫描已忽略输出。
- 安全忽略展示聚合：`ReportDialog` 已忽略项改为"已安全忽略 N 个"摘要 + 仅展示前 50 条 + "超过 50 条仅显示前 50"提示（新增 `CountGreaterThanConverter`），避免生成文件数百条刷爆 UI。

### 新增测试
- 单元（新增 12 项 → 87/87）：BUILD-ROOT-01（唯一 .slnx→.slnx）、02（唯一 .sln）、03（无 sln 子目录唯一 csproj→选中）、04（无 sln 多 csproj→歧义）、04b（多 csproj 主应用匹配→选中）、04c（多 sln 仓库名匹配→选中）、04d（多 sln 无匹配→歧义）、05（非 .NET→None）、05b（空仓库→None）、06（中文+空格路径 sln 解析）、IsDotNetProject 三分支、bin/obj 排除不影响解析。
- E2E（22/22）：T15（真实结构 .slnx + src/tests 子目录构建 PASS，Build Target=MyApp.slnx）、T16（中文+空格路径 sln 构建 PASS）、T17（无 .NET 项目→Info 跳过，不报 MSB1009）、T18（多 csproj 歧义→Warning 需人工选择，不阻断提交）。

### 验证
- Debug/Release 0 警告 0 错误；单测 87/87（201 assertions）；E2E 22/22；GUI 冒烟（主窗口+4 对话框，单测内置）。
- **真实只读自检查 E:\SafeGitPublisher**（邀请 console 程序直接运行 PreflightService）：Git 仓库 PASS / Git 作者 PASS（VkDream LOCAL）/ Remote PASS（origin → VkDream/SafeGitPublisher）/ 分支 main PASS / **Build PASS，Build Target=SafeGitPublisher.slnx（dotnet build SafeGitPublisher.slnx）**；生成推荐 .gitignore 后 bin/obj 不再进变更（74 项），敏感文件 PASS，Secret Scan PASS；`.gitignore` 已实际生成。
- **真实 self-publish 验证**：用户经 GUI 对 E:\SafeGitPublisher 真实 commit + push 成功（commit `eee477742188c62c1d5c6e51f4c76b9a0cfeac69`，`release: SafeGitPublisher v1.0.0`），GitHub 远端 `VkDream/SafeGitPublisher` main 分支已接收。
- 待用户项已收窄为：DPI 100/125/150 目视验收（尚未确认）、v1.0.0 Git Tag / GitHub Release / 可下载发布包（下一步 Release Distribution）。

### self-host 缺陷修复（V1.0.0 封版后，真实现场暴露）
- **现象（用户真实现场）**：成功 commit + push 后工作区归零，但顶部显示 PUBLISH BLOCKED，日志末尾 `BLOCKED dotnet build SafeGitPublisher.slnx 失败`。现场复现为不稳定/不可复现，未伪造结论；按合同完成逻辑修复与回归。
- **根因**：`MainViewModel.PublishAsync` 的 finally 中无条件 `await RunChecksAsync()` 刷新真实状态（此行为正确保留）；刷新时全量预检把 Build Gate 无条件绑定到"本次检查"——0 个可提交变更时仍真实执行 `dotnet build`，一旦该构建失败（现场可能与运行中 exe 输出占用、无变更语义下的环境抖动有关，无法稳定复现）即产生 Build Blocked → 误显示 PUBLISH BLOCKED。
- **修复合同（Build Gate 只针对"存在可提交变更"的发布候选）**：
  - 0 个可提交变更 → Build 不执行（`BuildRun=false`、`SkipReason="当前无可提交变更（0 个可提交变更），跳过构建门禁（Not Required）。"`），报告为 Info（跳过构建验证），不产生任何阻断；
  - 存在可提交变更 → Build Gate 原样强制执行，失败仍 `blocksPush` 阻断发布（安全未削弱）；
  - Banner 合同：0 变更 → `UP TO DATE`（不再 PUBLISH BLOCKED）；仓库级致命异常（git 不可用 / 非 Git 仓库 / 合并冲突 status）即使 0 变更也保留 `BLOCKED`。
- **实现**：
  - 新增 `Services\PublishBannerEvaluator.cs`（纯函数，`PublishBannerKind`：Hidden/Blocked/UpToDate/ReviewRequired/Ready；仓库级致命项 `git_available`/`repo_detected`/`status` 优先；0 变更 → UpToDate）；
  - `MainViewModel.UpdatePublishBanner` 改用该评估器；`PreflightService` build 段 0 变更跳过；
  - 构建失败日志增强：`dotnet build <target> 失败（ExitCode=N）` + 关键错误行前 3 条（不刷大量 MSBuild 输出）；
  - 修复重构引入的回归：有变更分支丢失 `ctx.DotNetProject=true` 赋值（T11/T15 恢复）。
- **新增测试**：单测 +12（B01~B12 Banner 合同：0 变更 + Build/Image/Commit 级 Gate Blocked → 仍 UpToDate；仓库致命 → 仍 Blocked；有变更 → Blocked/ReviewRequired/Ready；发布后刷新 → UpToDate）→ 99/99（214 assertions）；E2E +2（Z07 0 变更 + 必败 csproj → Build Gate 整体跳过不阻断；Z08 成功 commit+push 后刷新 → 0 变更 → UP TO DATE）→ 24/24。
- **验证**：Debug/Release 0 警告 0 错误；单测 99/99（214 assertions）；E2E 24/24；GUI 冒烟通过（单测内置）。

### self-host 缺陷修复 2（V1.0.0 Git Tag 前：Self-Build 隔离输出 + .serena 元数据门禁）
- **现象（用户真实现场，有真实证据）**：SafeGitPublisher.exe 自身运行中，E:\SafeGitPublisher 存在真实待提交变更 → Build Gate 必须执行（合同正确）→ 传统 `dotnet build` 输出触碰运行中的自身 EXE → `error MSB3027/MSB3021 无法将 obj\Debug\net10.0-windows\apphost.exe 复制到 bin\Debug\net10.0-windows\SafeGitPublisher.exe，文件被另一进程锁定` → Build FAIL → PUBLISH BLOCKED，self-host 无法闭环。
- **根因（已自动化复现）**：Windows 文件占用——传统 build 的 apphost copy 阶段需要覆盖正在运行的 EXE，MSBuild 重试 10 次后报 MSB3027（配套 MSB3021）。本机自动化复现成功：锁定目标 EXE（FileShare.None）→ 传统 build --no-incremental → MSB3027/MSB3021 + exit=1；同锁存在时隔离构建 → exit=0 PASS。
- **修复（Self-Host Isolated Build）**：
  - 每次 Preflight Build 使用官方 `dotnet build --artifacts-path %TEMP%\SafeGitPublisher\PreflightBuild\<GUID>`（.NET 8+ artifacts output layout，SDK 10.0.302 实测支持）：全部 bin/obj/apphost/exe 输出进入隔离临时根，源码树零污染、绝不覆盖运行中的自身 EXE；
  - 新增 `Services\TempBuildRoot.cs`（唯一目录创建 + best-effort 清理，双护栏只删 GUID 目录、绝不递归删用户目录；清理失败不改判构建结果，仅 Warning 提示）；
  - `DotNetBuildService`：隔离目录创建失败时明确返回未执行原因（绝不静默降级为传统构建）；`BuildResult` 新增 BuildMode（Isolated Temporary Output）/ IsolationRoot / CleanupFailed；CommandSummary 显示 `dotnet build <target>（隔离输出）`；Build FAIL 日志增强（ExitCode + 错误前 3 条）保留不回退；
  - Build Target Resolution / 非 .NET Skip / Ambiguous / Timeout / Cancel / Warning 语义全部保留；隔离不是假构建：真实编译错误仍 Build FAIL + blocksPush。
- **.serena 元数据门禁**：`.serena/` 纳入本机 AI/开发工具元数据策略（与 .claude/ .reasonix/ 同级）——GitIgnoreService.RequiredRules + SensitiveFileRules.BlockedDirectoryNames（路径语义，任意层级；不误伤 docs/serena-notes.md、src/SerenaParser.cs 等合法业务文件）+ 根 .gitignore 实际加入 `.serena/`（git check-ignore -v 验证命中 .gitignore:24）。
- **新增测试**：单测 +9（SERENA-01/01b/01c 敏感规则、SERENA-02/02b 推荐规则、TempBuildRoot TBR01~04 目录安全与幂等清理）→ 108/108（238 assertions）；E2E +2（S01 锁定 EXE 时传统 build 复现 MSB3027 + 隔离构建 PASS + 源码树零污染 + 临时目录清理；S02 .serena 被忽略后不进变更/不 Sensitive/不 Secret 扫描）→ 26/26；T11/T15/T16 增强隔离模式断言（SELFBUILD-02/03/04）。
- **验证**：Debug/Release 0 警告 0 错误；单测 108/108；E2E 26/26；GUI 冒烟通过（单测内置）；**真实 self-host 专项**：Debug exe 运行中（PID 实测）+ E:\SafeGitPublisher 16 个真实变更 → Preflight BuildRun=true、Build Target=SafeGitPublisher.slnx、Build Mode=Isolated Temporary Output、Build PASS（Exit=0，4.4s）、无 MSB3027/MSB3021、CanPush=True、`.serena/` 不在本次变更。

## v1.0.1（历史修复记录）

- WPF `Run.Text` 默认 TwoWay 绑定导致启动崩溃（对只读属性 TwoWay 绑定）：全部 6 处 `Run.Text` 显式 `Mode=OneWay`。

## v1.0.0-beta（历史，早期功能基线）

- 12 项安全 Gate、首次发布向导、设置持久化、Secret 脱敏、双层暂存扫描、危险命令黑名单等（详见 README）。
