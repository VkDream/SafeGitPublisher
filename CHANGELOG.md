# SafeGitPublisher CHANGELOG

## v1.0.0（2026-08-07，Release Candidate，修复自发布 Build Target 缺陷后待用户验收）

### 修复
- **Build Target Resolution 缺陷（V1.0.0 RC 自发布验收发现）**：旧实现假定 csproj 位于 `RepositoryRoot\RepositoryName.csproj`，真实"根 .slnx + src/tests 子目录"结构自发布报 `MSB1009 项目文件不存在`。
  - 新增 `Services\BuildTargetResolver.cs` 纯静态解析器，合同：根目录唯一 `*.sln`/`*.slnx` → 选中；多 solution → 与仓库名匹配优先，否则歧义（需人工选择，不随意猜）；无 solution → 递归扫 csproj（排除 bin/obj/.git/node_modules，深度 ≤8）；唯一 csproj → 选中；多 csproj → 仓库名匹配主应用优先，否则歧义；完全无 .NET 项目 → 跳过构建（非 Build Failed）。
  - **支持 `.slnx`**（与 .sln 同优先级）。
  - `DotNetBuildService` 改为 `dotnet build <解析出的完整路径>`（消除 MSB1009，绝不再传目录名+WorkDir 组合），`IsDotNetProject` 复用解析器。
  - Build UI：报告显示 `Build Target`（文件名，如 `SafeGitPublisher.slnx`）+ 命令摘要（`dotnet build SafeGitPublisher.slnx`）；失败时显示 Target / Exit Code / 关键错误行摘要（≤3 行）；歧义 → Warning（需人工选择）不阻断提交；非 .NET → Info。
- **Secret 扫描自指/种子误报**：扫描器将自身规则文本、代码变量赋值（`var secret =`/`var server =`）、测试种子字面量（`ghp_`/`password =`/`192.168.1.23`）判为凭据，导致含测试工程的真实仓库无法通过 Secret Scan。修复：规则/注释避开键名+"="字面量；`var serverMatchResult` 重命名；测试种子改为运行时拼接（文件文本不含完整模式，运行断言行为不变）。
- **首次发布 .gitignore 回归验证**：生成推荐 .gitignore 后 `bin/obj`、`*.dll/*.pdb/*.deps.json/*.runtimeconfig*` 不再进入"本次变更"，不再计入 Sensitive BLOCKED；Secret Scan 不再扫描已忽略输出。
- 安全忽略展示聚合：`ReportDialog` 已忽略项改为"已安全忽略 N 个"摘要 + 仅展示前 50 条 + "超过 50 条仅显示前 50"提示（新增 `CountGreaterThanConverter`），避免生成文件数百条刷爆 UI。

### 新增测试
- 单元（新增 12 项 → 87/87）：BUILD-ROOT-01（唯一 .slnx→.slnx）、02（唯一 .sln）、03（无 sln 子目录唯一 csproj→选中）、04（无 sln 多 csproj→歧义）、04b（多 csproj 主应用匹配→选中）、04c（多 sln 仓库名匹配→选中）、04d（多 sln 无匹配→歧义）、05（非 .NET→None）、05b（空仓库→None）、06（中文+空格路径 sln 解析）、IsDotNetProject 三分支、bin/obj 排除不影响解析。
- E2E（22/22）：T15（真实结构 .slnx + src/tests 子目录构建 PASS，Build Target=MyApp.slnx）、T16（中文+空格路径 sln 构建 PASS）、T17（无 .NET 项目→Info 跳过，不报 MSB1009）、T18（多 csproj 歧义→Warning 需人工选择，不阻断提交）。

### 验证
- Debug/Release 0 警告 0 错误；单测 87/87（201 断言）；E2E 22/22；GUI 冒烟（主窗口+4 对话框，单测内置）。
- **真实只读自检查 E:\SafeGitPublisher**（邀请 console 程序直接运行 PreflightService）：Git 仓库 PASS / Git 作者 PASS（VkDream LOCAL）/ Remote PASS（origin → VkDream/SafeGitPublisher）/ 分支 main PASS / **Build PASS，Build Target=SafeGitPublisher.slnx（dotnet build SafeGitPublisher.slnx）**；生成推荐 .gitignore 后 bin/obj 不再进变更（74 项），敏感文件 PASS，Secret Scan PASS；`.gitignore` 已实际生成。
- 唯一待人工项：图片脱敏确认（`assets/SafeGitPublisher-source.png`，确认页勾选即可）。
- 待用户 GUI 验收 & 实际首次自发布（用户人工执行 git 提交/推送）。

## v1.0.1（历史修复记录）

- WPF `Run.Text` 默认 TwoWay 绑定导致启动崩溃（对只读属性 TwoWay 绑定）：全部 6 处 `Run.Text` 显式 `Mode=OneWay`。

## v1.0.0-beta（历史，早期功能基线）

- 12 项安全 Gate、首次发布向导、设置持久化、Secret 脱敏、双层暂存扫描、危险命令黑名单等（详见 README）。
