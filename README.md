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
| 6 | Secret 扫描 | github_pat_/ghp_/sk-/AKIA/Bearer → Blocked；赋值型 secret/password 字面量 → High；内网 IP/非本机 Server → Warning；关键字 → Info | 提交 + Push |
| 7 | 大文件 | >10MB Warning、>50MB 高危 Warning、>100MB（GitHub 硬限制）→ Blocked | 提交 + Push |
| 8 | Git 作者 | 与推荐身份不一致 → Warning（一键应用，只写仓库 local 配置） | 不阻断 |
| 9 | Remote | 未配置 origin → Warning（禁 Push，可设置）；地址畸形（https\:// 等）→ Blocked | Push |
| 10 | 分支 | master / detached HEAD → Info 提示 | 不阻断 |
| 11 | 图片脱敏 | 本次含新增/修改图片 → Warning 并禁 Push，需勾选"图片已完成脱敏检查" | Push |
| 12 | 构建 | .NET 项目 build 失败 → Blocked（禁 Push）；构建警告 → Warning | Push |

规则：
- **BLOCKED = 禁 Push**（部分检查同时禁提交）；**WARNING = 复核后确认可继续**。
- 提交流程二次闸门：`git add --all` 后再扫描已暂存内容，发现 Blocked 项自动 `git reset` 取消暂存并中止。
- 危险命令黑名单（force push / rebase / reset --hard / clean -fd / filter-repo / branch -D / restore / checkout -- . 等）**绝不自动执行**。
- Push 仅使用普通 `git push` 或首次 `git push -u origin <branch>`；同步远端仅使用 `git pull --ff-only`，从不自动 merge。
- 所有 Secret 输出统一脱敏（保留公开前缀 + **** + 末尾 4 位），日志与界面永不出现凭据原文。

## 二、界面功能

- 最近项目（10 个）+ 选择项目 → 自动检查
- 12 项检查结果列表（✅/⚠/🚫 + 颜色）+ 一键修复按钮
- 变更文件列表（状态/大小/风险）与详细报告对话框
- 提交信息前缀（feat:/fix:/docs:/refactor:/chore:/test:）
- 两个发布按钮：**仅提交** / **安全提交并上传**，均带最终确认页
- 首次发布向导：git init → .gitignore → 作者身份 → 检查 → 设置 origin → 发布
- 设置对话框（大文件阈值、构建开关、图片确认开关、推荐作者）

## 三、目录结构

```
E:\SafeGitPublisher
├─ SafeGitPublisher.slnx
├─ src\SafeGitPublisher            # WPF 主工程（net10.0-windows / x64 / WinExe）
│  ├─ Models\                      # 领域模型（报告、变更、设置等）
│  ├─ Services\                    # 进程/ Git / 扫描 / 检查 / 发布服务
│  ├─ ViewModels\                  # MVVM
│  └─ Views\                       # 主窗口 + 对话框（XAML 可视化界面）
├─ tests\SafeGitPublisher.Tests    # 零依赖控制台单测（75 项，含 GUI 启动冒烟）
└─ tests\SafeGitPublisher.E2E      # 真实 git 临时仓库端到端测试（19 项）
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

## 六、测试结果（2026-08-07，V1.0.0 Release Candidate，Build Target 修复后）

- 单元测试：87/87 通过（新增 BUILD-ROOT-01~06 Build Target 解析 12 项；含 Secret 规则、敏感文件规则、URL 解析、porcelain 解析、.gitignore、大文件分级、身份、报告决策、Zero Change Gate 逻辑、版本元数据、应用图标、GUI 冒烟）
- E2E：22/22 通过（新增 T15 自发布结构 .slnx 构建 PASS / T16 中文+空格路径 sln 构建 PASS / T17 无 .NET 项目跳过构建 / T18 多 csproj 歧义需人工选择）
- Release 构建：0 警告 0 错误；ProductVersion=1.0.0 / FileVersion=1.0.0.0
- GUI 冒烟（tests\SafeGitPublisher.Tests\GuiSmokeHost.cs）：单 STA 线程 + 单 Application 实例顺序执行主窗口 + 关于/设置/详细报告/最终确认 4 对话框，捕获 DispatcherUnhandledException 与 DataBinding 运行期错误；通过
- 真实只读自检查 E:\SafeGitPublisher（PreflightService 全链路，含真实 dotnet build）：Git 仓库/作者/Remote(origin → VkDream/SafeGitPublisher)/分支 main/Build PASS，Build Target=SafeGitPublisher.slnx；生成推荐 .gitignore 后 bin/obj 不再进变更，敏感文件 PASS、Secret Scan PASS

> **Build Target Resolution（V1.0.0 修复）**：不再假定 csproj 位于仓库根目录。解析规则（`Services\BuildTargetResolver.cs`）：
> 根目录唯一 `*.sln`/`*.slnx` → 构建该 solution（支持 .slnx）｜多 solution → 与仓库名匹配优先，否则需人工选择｜无 solution → 递归搜 csproj｜唯一 csproj → 构建（子目录亦可）｜多 csproj → 仓库名匹配主应用优先，否则需人工选择｜完全无 .NET 项目 → 跳过构建（Info，不报 MSB1009）。
> 报告显示 Build Target 文件名 + 命令摘要（如 `dotnet build SafeGitPublisher.slnx`）；失败时展示 Target / Exit Code / 关键错误摘要。

- 静态检查：全部 XAML 走可视化设计、无硬编码版本（统一 AppVersionService 程序集元数据）、对话框空列表均有空状态
- Secret 扫描范围：只扫真正可提交的文本候选（二进制/编译输出 dll/pdb/obj/gitignore 排除）；无真实 Secret 时 PASS（测试种子改用运行时拼接，文件内不含完整凭据模式）

> **Zero Change Gate（V1.0.0 新增，人工验收 Bug 修复）**：0 个可提交变更时，无论提交说明是否填写（含 "test:"），CanCommit=CanPush=false，确认页不会打开。
> 双层防护：① ViewModel/UI 层 CanExecute 实时响应变更数/提交说明/检查结果/忙碌/图片确认（Tooltip 说明禁用原因）；② WorkflowService 打开确认页前重新读取真实 `git status`，0 变更即中止（INFO 提示，非错误红叉），路径集合与最近检查不一致时要求重新检查。

## 七、已知限制（V1）

- 只针对 GitHub（https / git@ / ssh:// 格式解析）；其他托管平台仅提示"非 GitHub 标准 URL"。
- 图片脱敏确认为人工确认制（工具不进行图像内容识别）。
- 构建检查只支持 .NET 项目（识别 csproj/sln）；其他语言项目显示"跳过构建"。
- 危险命令（force push、rebase、reset --hard 等）不提供自动化执行入口。
- 不管理 Token 凭据、不调用 GitHub REST API（Push 由 git.exe 完成，是否输入账号密码由 git 自身凭据管理器处理）。
