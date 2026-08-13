using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Tests;

/// <summary>
/// 已创建提交的“仅上传”恢复流程合同。
/// 这些测试只读取源码或执行纯属性逻辑，不启动 Git、网络或 WPF 窗口。
/// </summary>
public static class PendingPushRecoveryContractTests
{
    private static string ReadSource(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(PendingPushRecoveryContractTests).Assembly.Location) ?? string.Empty,
            "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()), Encoding.UTF8);
    }

    private static string ExtractBlock(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"应找到源码合同标记：{marker}");
        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"源码合同标记后应存在方法体：{marker}");

        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}') depth--;
            if (depth == 0) return source[start..(index + 1)];
        }

        throw new Exception($"源码合同标记的方法体未闭合：{marker}");
    }

    private static string ExtractStatement(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"应找到源码合同标记：{marker}");
        var end = source.IndexOf(';', start);
        Assert.True(end > start, $"源码合同标记后应存在语句终止符：{marker}");
        return source[start..(end + 1)];
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"应找到源码合同起始标记：{startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"应找到源码合同结束标记：{endMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    [Test]
    public static void OrdinaryPublish_ProbesRemoteBeforeIndexMutationAndCommit()
    {
        var source = ReadSource("src", "SafeGitPublisher", "Services", "PublishWorkflowService.cs");
        var execute = ExtractBlock(source, "public async Task<PublishResult> ExecuteAsync(");
        var probe = execute.IndexOf("ReadRemoteBranchSnapshotAsync", StringComparison.Ordinal);
        var add = execute.IndexOf("_git.AddAllAsync", StringComparison.Ordinal);
        var commit = execute.IndexOf("_git.CommitAsync", StringComparison.Ordinal);

        Assert.True(probe >= 0, "普通提交并上传必须在创建本地提交前探测远端；网络不可达时应失败关闭");
        Assert.True(add >= 0 && probe < add, "远端探测必须发生在 git add 前，避免网络已知不可用时改动 index");
        Assert.True(commit >= 0 && probe < commit, "远端探测必须发生在 git commit 前，避免产生明知无法进入上传阶段的新提交");

        var probeHelper = ExtractBlock(source, "private async Task<RemoteBranchSnapshot> ReadRemoteBranchSnapshotAsync(");
        Assert.Contains(probeHelper, "_git.RemoteBranchOidAsync", "发布前远端探测必须执行真实 ls-remote 查询，不能只读本地配置");
    }

    [Test]
    public static void GitPushAndOutgoingRange_AreBoundToFullObjectIdsInsteadOfHead()
    {
        var source = ReadSource("src", "SafeGitPublisher", "Services", "GitService.cs");
        var push = ExtractBlock(source, "public async Task<CommandResult> PushExplicitTargetAsync(");
        var outgoing = ExtractBlock(source, "public async Task<CommandResult> OutgoingCommitsFromRemoteOidAsync(");

        Assert.Contains(push, "string sourceOid", "Push API 必须显式接收已核对的完整提交 OID");
        Assert.Contains(push, "{sourceOid}:refs/heads/{branch}", "Push refspec 必须由固定 OID 构造");
        Assert.NotContains(push, "HEAD:refs/heads", "Push 执行阶段不得重新解析可漂移的 HEAD");

        var objectId = ExtractStatement(source, "private static bool IsObjectId(");
        Assert.Contains(objectId, "value.Length is 40 or 64", "完整 Git OID 只能是 40 位 SHA-1 或 64 位 SHA-256，不能接受中间长度缩写");

        Assert.Contains(outgoing, "string lockedLocalOid", "outgoing 查询必须显式接收固定的本地提交 OID");
        Assert.Contains(outgoing, "{remoteOid}..{lockedLocalOid}", "outgoing 范围上界必须绑定固定 OID");
        Assert.NotContains(outgoing, "..HEAD", "outgoing 查询不得使用执行时可能漂移的 HEAD");
    }

    [Test]
    public static void ExistingPushRecovery_NeverStagesCreatesOrRestoresACommit()
    {
        var source = ReadSource("src", "SafeGitPublisher", "Services", "PublishWorkflowService.cs");
        var prepare = ExtractBlock(source, "public async Task<ExistingPushPlan> PrepareExistingPushAsync(");
        var execute = ExtractBlock(source, "public async Task<PublishResult> ExecuteExistingPushAsync(");
        var recovery = prepare + execute;

        Assert.NotContains(recovery, "AddAllAsync", "仅上传恢复不得执行 git add");
        Assert.NotContains(recovery, "CommitAsync", "仅上传恢复不得再次创建提交");
        Assert.NotContains(recovery, "ReadTreeAsync", "仅上传恢复不得改写 index");
        Assert.NotContains(recovery, "RestoreIndexTreeAsync", "仅上传恢复不得恢复或改写 index");
        Assert.NotContains(recovery, "WriteIndexTreeAsync", "仅上传恢复不得创建 index 快照");
    }

    [Test]
    public static void ExistingPushExecution_ReconcilesRemoteBeforeAnyPushAndRechecksDrift()
    {
        var source = ReadSource("src", "SafeGitPublisher", "Services", "PublishWorkflowService.cs");
        var execute = ExtractBlock(source, "public async Task<PublishResult> ExecuteExistingPushAsync(");
        var head = execute.IndexOf("HeadOidResultAsync", StringComparison.Ordinal);
        var branch = execute.IndexOf("CurrentBranchResultAsync", StringComparison.Ordinal);
        var target = execute.IndexOf("ReadValidatedOriginAsync", StringComparison.Ordinal);
        var reconcile = execute.IndexOf("ReadRemoteBranchSnapshotAsync", StringComparison.Ordinal);
        var push = execute.IndexOf("PushExplicitTargetAsync", StringComparison.Ordinal);

        Assert.True(head >= 0 && branch >= 0 && target >= 0, "执行仅上传前必须复核 HEAD、分支和远端目标漂移");
        Assert.True(reconcile >= 0 && push >= 0 && reconcile < push, "任何重复上传前必须先查询远端提交状态");
        Assert.Contains(execute, "RemoteTargetFingerprint", "执行阶段的公开请求只能携带目标指纹");
        Assert.Contains(execute, "CommitOid", "执行阶段必须使用准备阶段固定的完整提交 OID");
    }

    [Test]
    public static void IndeterminatePush_IsPersistedAsUnknownAndRequiresReconciliation()
    {
        var resultSource = ReadSource("src", "SafeGitPublisher", "Models", "PublishResult.cs");
        var enumSource = ReadSource("src", "SafeGitPublisher", "Models", "PendingPushPlan.cs");
        var workflowSource = ReadSource("src", "SafeGitPublisher", "Services", "PublishWorkflowService.cs");
        var ordinary = ExtractBlock(workflowSource, "public async Task<PublishResult> ExecuteAsync(");
        var recovery = ExtractBlock(workflowSource, "public async Task<PublishResult> ExecuteExistingPushAsync(");
        var ordinaryFailure = ExtractBetween(ordinary, "if (!push.Success)", "var pushedSnapshot");
        var recoveryCancel = ExtractBetween(recovery, "if (push.Canceled || push.TimedOut)", "if (!push.Success)");
        var unknownHelper = ExtractBlock(workflowSource, "private static PublishResult UnknownPush(");

        Assert.Contains(enumSource, "enum PushDeliveryState", "发布结果必须能区分未上传、成功与远端状态未知");
        Assert.Contains(enumSource, "Unknown", "取消或超时时必须有 Unknown 状态，不能误报为确定失败");
        Assert.Contains(resultSource, "PushDeliveryState PushState", "发布结果必须携带可审计的 Push 状态");
        Assert.Contains(resultSource, "RequiresPushReconciliation", "未知状态必须显式要求远端核对");
        Assert.Contains(ordinaryFailure, "UnknownPush(",
            "普通发布的 Push 任何非成功结果（含取消/超时）都必须走统一 Unknown 结果");
        Assert.Contains(recoveryCancel, "UnknownPush(", "仅上传恢复的 Push 取消/超时必须走统一 Unknown 结果");
        Assert.Contains(unknownHelper, "PushState = PushDeliveryState.Unknown", "统一未知结果必须标记 Unknown");
        Assert.Contains(unknownHelper, "RequiresPushReconciliation = true", "统一未知结果必须要求先核对远端，不得盲目重复 Push");
    }

    [Test]
    public static void ExistingPushPlan_PersistsOnlyTargetFingerprintAndSafeDisplay()
    {
        var modelSource = ReadSource("src", "SafeGitPublisher", "Models", "PendingPushPlan.cs");
        Assert.Contains(modelSource, "class ExistingPushPlan", "应存在公开安全计划 ExistingPushPlan");
        Assert.Contains(modelSource, "RemoteTargetFingerprint", "恢复计划必须绑定远端目标指纹");
        Assert.Contains(modelSource, "RemoteDisplay", "恢复计划只向 UI 暴露脱敏后的目标显示值");

        var unsafePublicTarget = Regex.IsMatch(modelSource,
            @"public\s+(?:required\s+)?string\??\s+(?:ExactTarget|ExactUrl|PushUrl|Credentials?|AccessToken|Token)\b",
            RegexOptions.CultureInvariant);
        Assert.True(!unsafePublicTarget, "恢复计划/请求不得持久化或公开精确 URL、凭据或 Token");

        var workflowSource = ReadSource("src", "SafeGitPublisher", "Services", "PublishWorkflowService.cs");
        var ticket = ExtractStatement(workflowSource, "private sealed record ExistingPushTicket(");
        Assert.NotContains(ticket, "ExactTarget", "内部短期票据也不得保存可能包含凭据的精确 Remote URL");
        Assert.Contains(ticket, "TargetFingerprint", "内部短期票据只应保存不可逆目标指纹用于执行前复核");
    }

    [Test]
    public static void ExistingPushPlan_CanExecuteOnlyReadySingleUseTicket()
    {
        var noTicket = new ExistingPushPlan { Disposition = ExistingPushDisposition.Ready };
        var blockedWithTicket = new ExistingPushPlan { Disposition = ExistingPushDisposition.Blocked, PlanId = "one-time-ticket" };
        var ready = new ExistingPushPlan { Disposition = ExistingPushDisposition.Ready, PlanId = "one-time-ticket" };

        Assert.True(!noTicket.CanExecute, "Ready 但没有一次性票据时不得执行");
        Assert.True(!blockedWithTicket.CanExecute, "非 Ready 状态即使意外带票据也不得执行");
        Assert.True(ready.CanExecute, "只有 Ready 且带一次性票据的安全计划可进入确认执行");

        var publicNames = typeof(ExistingPushPlan).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.True(!publicNames.Contains("ExactTarget") && !publicNames.Contains("ExactUrl") && !publicNames.Contains("PushUrl"),
            "公开恢复计划不得通过属性暴露精确 Remote URL");
    }

    [Test]
    public static void ExistingPushPrepare_FailsClosedWhenRequiredBuildProofIsUnavailable()
    {
        var modelSource = ReadSource("src", "SafeGitPublisher", "Models", "PendingPushPlan.cs");
        var workflowSource = ReadSource("src", "SafeGitPublisher", "Services", "PublishWorkflowService.cs");
        var prepare = ExtractBlock(workflowSource, "public async Task<ExistingPushPlan> PrepareExistingPushAsync(");
        var execute = ExtractBlock(workflowSource, "public async Task<PublishResult> ExecuteExistingPushAsync(");

        Assert.Contains(modelSource, "RequireBuildVerification", "仅上传准备请求必须携带当前构建策略");
        Assert.Contains(modelSource, "BuildVerifiedCommitOid", "构建证明必须绑定一个完整 commit OID，不能只使用布尔值");
        Assert.Contains(prepare, "request.RequireBuildVerification &&", "设置要求构建时必须进入构建证明 Gate");
        Assert.Contains(prepare, "!FixedEquals(lockedOid, request.BuildVerifiedCommitOid)",
            "构建证明必须与服务刚锁定的 HEAD OID 固定时序比较");
        Assert.Contains(prepare, "ExistingPushDisposition.Blocked", "缺少所需构建证明不得降级成警告或继续 Push");

        var viewModelSource = ReadSource("src", "SafeGitPublisher", "ViewModels", "MainViewModel.cs");
        var command = ExtractBlock(viewModelSource, "private async Task PushExistingCommitAsync(");
        Assert.Contains(command,
            "var requireBuildVerification = _settings.BuildBeforeCommit && buildTarget?.Kind != BuildTargetKind.None",
            "UI 必须按当前设置和已解析构建目标生成本次 Build Gate，不能依赖请求默认 false 绕过策略");
        Assert.Contains(command, "BuildTargetResolver.Resolve(root)",
            "仅上传的 Build 策略必须依据本次仓库重新解析，不能复用旧预检上下文");
        Assert.Contains(command, "BuildVerifiedCommitOid = buildVerifiedCommitOid",
            "UI 必须显式传递与当前提交绑定的构建证明；无证明时传 null 并由服务阻断");
        Assert.Contains(command, "string.Equals(headBefore, headAfter, StringComparison.Ordinal)",
            "构建前后 HEAD 漂移必须使构建证明失效");
        Assert.True(CountOccurrences(command, "StatusPorcelainAsync") >= 2,
            "构建前后都必须确认工作区干净，不能把混入未提交内容的构建结果绑定给 HEAD");
        // 源码块提取器不解析 C# 插值字符串中的花括号；这里对完整 ViewModel 做唯一字段计数，
        // 避免方法内自然语言插值被误当成方法边界而产生假失败。
        Assert.True(CountOccurrences(viewModelSource, "RequireBuildVerification = requireBuildVerification") >= 2,
            "准备与执行请求都必须携带同一当前 Build 策略，以便确认期间设置漂移失败关闭");
        Assert.Contains(execute, "ticket.RequireBuildVerification != request.RequireBuildVerification",
            "执行阶段必须比较一次性票据与当前请求的 Build 策略，禁止确认期间降级");
    }

    [Test]
    public static void PushPendingCommand_IsIndependentFromOriginalZeroChangeGate()
    {
        var gateSource = ReadSource("src", "SafeGitPublisher", "Services", "PublishGateEvaluator.cs");
        var viewModelSource = ReadSource("src", "SafeGitPublisher", "ViewModels", "MainViewModel.cs");
        var ordinaryGate = ExtractBlock(gateSource, "public static GateResult Evaluate(");

        Assert.Contains(ordinaryGate, "committableChanges > 0", "原提交/上传 Gate 必须继续要求至少一个可提交变更");
        Assert.Contains(viewModelSource,
            "PushExistingCommitCommand = new AsyncRelayCommand(_ => PushExistingCommitAsync(), _ => CanPushExistingCommit",
            "仅上传命令必须使用独立 CanPushExistingCommit 条件，不能复用普通 CanPush 的 0 变更门禁");
        Assert.Contains(viewModelSource,
            "public bool CanPushExistingCommit => CanOperate && HasExistingCommitRecovery;",
            "仅上传命令 Gate 只能依赖操作租约与仓库恢复入口，不能复用普通 CanPush 或工作区变更数");
        Assert.True(Regex.IsMatch(viewModelSource,
                @"SafeCommitAndPushCommand\s*=\s*new\s+AsyncRelayCommand\([^;]+CanOperate\s*&&\s*CanPush(?!Pending)",
                RegexOptions.CultureInvariant),
            "原安全提交并上传命令必须继续使用普通 CanPush Gate");
    }

    [Test]
    public static void PushOnlyDialog_ExplainsNoNewCommitAndMainButtonUsesDedicatedCommand()
    {
        var data = new ConfirmPublishData
        {
            RepositoryRoot = @"C:\repo",
            ProjectPath = @"C:\repo",
            PushExistingOnly = true,
            CommitOidDisplay = new string('a', 40),
            ChangeCount = 0
        };
        Assert.Contains(data.ActionSummary, "只上传", "确认页必须明确本次是仅上传");
        Assert.Contains(data.ActionSummary, "不会", "确认页必须明确不会重复暂存或提交");
        Assert.Contains(data.ActionSummary, "新提交", "确认页必须明确不会创建新提交");
        Assert.Equal("确认仅上传", data.ConfirmButtonText, "0 变更不应阻止仅上传确认按钮使用独立文案");
        Assert.Contains(data.ChangeDisplay, "不会重新暂存或提交", "只上传确认页不得暗示会处理工作区变更");

        var dialog = ReadSource("src", "SafeGitPublisher", "Views", "ConfirmPublishDialog.xaml");
        var dialogCodeBehind = ReadSource("src", "SafeGitPublisher", "Views", "ConfirmPublishDialog.xaml.cs");
        Assert.Contains(dialog, "Text=\"{Binding ActionSummary}\"", "确认框必须展示不可混淆的操作说明");
        Assert.Contains(dialog, "Content=\"{Binding ConfirmButtonText}\"", "确认按钮必须使用仅上传专属文案");
        Assert.Contains(dialog, "Value=\"{Binding CommitOidDisplay}\"", "确认框必须展示固定的待上传提交 OID");
        Assert.Contains(dialogCodeBehind, "_data.PushExistingOnly || _data.ChangeCount > 0",
            "只上传已有提交必须能在 0 工作区变更时独立确认，普通发布仍要求变更数大于 0");

        var mainWindow = ReadSource("src", "SafeGitPublisher", "Views", "MainWindow.xaml");
        Assert.Contains(mainWindow, "Content=\"检查并上传已有提交\"", "主界面必须提供明确的恢复入口");
        Assert.Contains(mainWindow, "Command=\"{Binding PushExistingCommitCommand}\"", "恢复按钮必须绑定独立命令");
        Assert.Contains(mainWindow, "IsEnabled=\"{Binding CanPushExistingCommit}\"", "恢复按钮不能绑定普通 CanPush Gate");
        Assert.Contains(mainWindow, "Visibility=\"{Binding HasExistingCommitRecovery", "仅在存在可恢复提交时显示恢复入口");
    }
}
