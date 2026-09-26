using System.Text;

using Godot;

using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

using STS2RitsuLib.Compat;

using Together.Core.Foundation;
using Together.Core.Settings;

namespace Together.Core.Diagnostics;

/// <summary>
/// 「一键导出当前环境」：把<b>启用的 mod 列表 + 本机设置 + 本局会话状态</b>写成一份 txt；
/// 并把"要发给作者的那几个文件"凑进同一个文件夹（反馈包）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：玩家反馈 bug 时最缺的从来不是截图，而是"他那边到底装了什么、设置开成什么样"。
/// 本体的 mod 列表只存在于"分歧报告"里（要真出 <c>State divergence</c> 才会生成），平时查不到；
/// 这份导出把同口径的信息随时写出来，放进<b>和 <c>godot.log</c> 同一个文件夹</b>。
/// 而"要发哪几个文件"这件事不该让玩家自己判断 —— <see cref="ExportBundle" /> 会把
/// <c>godot.log</c> + <c>together-modlist.txt</c>（+ 最新的分歧诊断包）复制到
/// <c>logs\together-feedback\</c>，玩家点一下按钮就拿到"要发的那些"。
/// </para>
/// <para>
/// <b>数据来源全是公开 API</b>：<c>ModManager.GetLoadedMods()</c>（本体的加载顺序，和分歧报告同一口径）、
/// RitsuLib 的 <c>RitsuModManager.GetKnownMods()</c>（含未加载 / 加载失败 / 被禁用的条目）。
/// 两条都拿不到时只打警告，绝不影响对局。
/// </para>
/// <para>
/// 全文<b>同时写进 log</b>（<c>[together][env]</c> 前缀）：玩家如果只会复制 log，现场也一样在。
/// 需要打开文件夹时会调本体的 <c>OS.ShellShowInFileManager</c>（和游戏自带的 <c>open logs</c> 同一条路）。
/// </para>
/// </remarks>
internal static class ModListExport
{
    /// <summary>导出文件名（固定名字，覆盖写：永远是"最近一次"的那份）。</summary>
    public const string FileName = "together-modlist.txt";

    /// <summary>反馈包文件夹名（放在日志目录里；固定名字，覆盖写）。</summary>
    public const string BundleFolderName = "together-feedback";

    /// <summary>反馈包里那份说明的文件名（玩家只看文件夹时也知道要发什么）。</summary>
    private const string ReadMeName = "先看我（要发哪些文件）.txt";

    /// <summary>
    /// 生成"反馈包"：先把环境快照写出来，再把<b>要发给作者的文件复制到同一个文件夹</b>里。
    /// </summary>
    /// <remarks>
    /// 玩家的操作成本降到最低：出问题时窗口里点「打开反馈文件夹」，看到的
    /// <c>logs\together-feedback\</c> 里就正好是"要发的那些" —— <c>godot.log</c>（现场）、
    /// <c>together-modlist.txt</c>（mod 列表 + 设置 + 会话）、以及（如果有）最新的分歧诊断包。
    /// 复制失败（比如 log 正被占用）不影响主流程：<c>together-modlist.txt</c> 本身一定在日志目录里，
    /// 这时返回日志目录。
    /// </remarks>
    public static string? ExportBundle(string reason, bool openFolder = false)
    {
        var listPath = Export(reason, openFolder: false);
        if (listPath is null)
        {
            return null;
        }

        try
        {
            var logs = Path.GetDirectoryName(listPath);
            if (string.IsNullOrEmpty(logs))
            {
                return listPath;
            }

            var bundle = Path.Combine(logs, BundleFolderName);
            Directory.CreateDirectory(bundle);

            CopyInto(bundle, listPath);
            CopyInto(bundle, Path.Combine(logs, "godot.log"));

            foreach (var archive in NewestFiles(logs, "ritsulib_state_divergence_*.zip", keep: 2))
            {
                CopyInto(bundle, archive);
            }

            File.WriteAllText(
                Path.Combine(bundle, ReadMeName),
                "Together 出问题的时候，作者最需要的是这两个文件（都在本文件夹里）：\n"
                + "  · godot.log            —— 游戏自己的运行日志，卡死 / 报错的现场就在最后几行\n"
                + "  · together-modlist.txt —— 启用的 mod 列表 + 本机生效设置 + 本局会话状态\n"
                + "（如果里面有 ritsulib_state_divergence_*.zip，也一起发）\n\n"
                + "  · 想反馈：把它们发到 mod 的创意工坊页面评论区，或者你所在的 mod 群\n"
                + "    再补一句\"当时在做什么\"（哪个事件 / 哪张牌 / 谁先按的）就够定位了\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Log.Info($"[together] 反馈包已生成（{reason}）：{bundle}");

            if (openFolder)
            {
                OpenInFileManager(bundle);
            }

            return bundle;
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 生成反馈包失败（退回日志目录）：{ex.GetType().Name}: {ex.Message}");

            if (openFolder)
            {
                OpenInFileManager(listPath);
            }

            return listPath;
        }
    }

    /// <summary>用系统文件管理器打开一个路径（失败只记一行，不抛）。</summary>
    private static void OpenInFileManager(string path)
    {
        try
        {
            var error = OS.ShellShowInFileManager(path);
            if (error != Error.Ok)
            {
                Log.Warn($"[together] 打开日志目录失败（{error}）：{path}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 打开日志目录异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CopyInto(string folder, string source)
    {
        try
        {
            if (!File.Exists(source))
            {
                return;
            }

            File.Copy(source, Path.Combine(folder, Path.GetFileName(source)), overwrite: true);
        }
        catch (Exception ex)
        {
            // 复制不了就跳过这一个（godot.log 可能正被写入占用）—— 主流程不受影响。
            Log.Warn($"[together] 反馈包复制失败（{Path.GetFileName(source)}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IEnumerable<string> NewestFiles(string folder, string pattern, int keep)
    {
        try
        {
            return Directory.GetFiles(folder, pattern)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(keep)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>只写 <see cref="FileName" /> 本身；返回文件绝对路径（失败返回 <c>null</c>）。</summary>
    /// <remarks>对外统一走 <see cref="ExportBundle" />（它会把要发的文件凑齐）；这个方法只负责"写清单 + 全文进 log"。</remarks>
    private static string? Export(string reason, bool openFolder)
    {
        try
        {
            var text = Build(reason);
            var directory = Path.Combine(OS.GetUserDataDir(), "logs");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, FileName);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Log.Info($"[together] 环境快照已导出（{reason}）：{path}");

            // 全文进 log：玩家只贴 log 的时候，mod 列表也不会丢。
            foreach (var line in text.Split('\n'))
            {
                Log.Info($"[together][env] {line.TrimEnd('\r')}");
            }

            if (openFolder)
            {
                try
                {
                    var error = OS.ShellShowInFileManager(path);
                    if (error != Error.Ok)
                    {
                        Log.Warn($"[together] 打开日志目录失败（{error}）：{directory}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[together] 打开日志目录异常：{ex.GetType().Name}: {ex.Message}");
                }
            }

            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"[together] 环境快照导出失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>拼出这份快照的正文（纯函数，便于测试 / 复用）。</summary>
    public static string Build(string reason)
    {
        var text = new StringBuilder();

        text.AppendLine("=== Together 环境快照 / environment snapshot ===");
        text.AppendLine($"导出时间 / time : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"触发原因 / why  : {reason}");
        text.AppendLine($"together        : v{Const.Version}");
        text.AppendLine($"RitsuLib        : {VersionOf("STS2-RitsuLib")}");
        text.AppendLine($"本机 netId      : {LocalContext.NetId}");
        text.AppendLine($"联机            : {DescribeNet()}");
        text.AppendLine($"合作组          : {DescribePair()}");
        text.AppendLine($"本局种子        : {DescribeSeed()}");
        text.AppendLine($"逐条取证        : TOGETHER_DRIFT={(DriftLog.Enabled ? "1（开）" : "未设置（关）")}");
        text.AppendLine($"生效设置        : {DescribeSettings()}");

        text.AppendLine();
        text.AppendLine("--- 已加载的 mod（按加载顺序，和分歧报告同一口径）---");
        AppendLoadedMods(text);

        text.AppendLine();
        text.AppendLine("--- 检测到的全部条目（含未加载 / 加载失败 / 被禁用）---");
        AppendKnownMods(text);

        text.AppendLine();
        text.AppendLine("如果你要反馈：把这份文件 + 同一个文件夹里的 godot.log 一起发过去就够了（要不要发、发到哪里由你决定）。");
        text.AppendLine("（完整 mod 清单以「已加载」那段为准；下面那段里 state != Loaded 的都能解释\"为什么某个 mod 没生效\"。）");

        return text.ToString();
    }

    private static void AppendLoadedMods(StringBuilder text)
    {
        try
        {
            var index = 0;
            foreach (var mod in ModManager.GetLoadedMods())
            {
                index++;
                var manifest = mod.manifest;
                text.AppendLine(
                    $"#{index:00} [{mod.modSource}] {manifest?.name ?? "<无清单>"} ({manifest?.id ?? "?"})"
                    + $" version={manifest?.version ?? mod.version?.ToString() ?? "?"}"
                    + (mod.workshopId is { } workshopId ? $" workshop={workshopId}" : string.Empty)
                    + $" affectsGameplay={manifest?.affectsGameplay.ToString() ?? "?"}"
                    + $" path={mod.path}");
            }

            if (index == 0)
            {
                text.AppendLine("（本体没报任何已加载 mod —— 可能未开启 mod 加载）");
            }
        }
        catch (Exception ex)
        {
            text.AppendLine($"（读 ModManager.GetLoadedMods() 失败：{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private static void AppendKnownMods(StringBuilder text)
    {
        try
        {
            foreach (var info in RitsuModManager.GetKnownMods())
            {
                text.AppendLine(
                    $"- [{info.State}] [{info.Source}] {info.Name} ({info.Id})"
                    + $" version={info.Version ?? "?"}"
                    + (info.WorkshopItemId is { } workshopId ? $" workshop={workshopId}" : string.Empty)
                    + $" affectsGameplay={info.AffectsGameplay}"
                    + (info.Errors.Count > 0
                        ? $" errors=[{string.Join(" | ", info.Errors.Select(error => error.ToString()))}]"
                        : string.Empty));
            }
        }
        catch (Exception ex)
        {
            text.AppendLine($"（读 RitsuModManager.GetKnownMods() 失败：{ex.GetType().Name}: {ex.Message}）");
        }
    }

    private static string VersionOf(string modId)
    {
        try
        {
            return RitsuModManager.TryGetModInfo(modId, out var info) && info is not null
                ? $"{info.Version ?? "?"}（{info.State}）"
                : "<未检测到>";
        }
        catch (Exception)
        {
            return "<读取失败>";
        }
    }

    private static string DescribeNet()
    {
        try
        {
            var net = RunManager.Instance?.NetService;
            if (net is null)
            {
                return "无 NetService（单机）";
            }

            return net.Type.IsMultiplayer()
                ? $"{net.Type}（本机 netId={net.NetId}）"
                : $"{net.Type}（非联机）";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.GetType().Name}";
        }
    }

    private static string DescribePair()
    {
        try
        {
            if (!TogetherPair.IsActive)
            {
                return TogetherPair.MemberCount >= TogetherPair.MinMembers
                    ? $"已配对但本局未激活（{TogetherPair.MemberCount} 人）"
                    : "无（本局未成组）";
            }

            var echoes = string.Join(",", TogetherPair.Echoes.Select(echo => echo.NetId));
            return $"anchor={TogetherPair.Anchor?.NetId} echo=[{echoes}] 共 {TogetherPair.MemberCount} 人";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.GetType().Name}";
        }
    }

    private static string DescribeSeed()
    {
        try
        {
            if (TogetherPair.Anchor?.RunState is not { } runState)
            {
                return "（还没进局）";
            }

            return runState.Rng.StringSeed;
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.GetType().Name}";
        }
    }

    private static string DescribeSettings()
    {
        try
        {
            return string.Join(
                " ",
                $"合作={On(TogetherSettingsSync.EffectiveSymbiosisEnabled)}",
                $"合并初始卡组={On(TogetherSettingsSync.EffectiveMergeStarterDecks)}",
                $"血上限+{TogetherSettingsSync.EffectiveHpBonusPercent}%",
                $"共享金币={On(TogetherSettingsSync.EffectiveShareGold)}",
                $"球位={TogetherSettingsSync.EffectiveOrbCap}",
                "兼容[",
                $"牌序={On(TogetherSettingsSync.EffectiveCompatDeterministicOrder)}",
                $"去重={On(TogetherSettingsSync.EffectiveCompatHookDedupe)}",
                $"放宽={On(TogetherSettingsSync.EffectiveCompatHookWiden)}",
                $"卡牌视图={On(TogetherSettingsSync.EffectiveCompatSharedCardView)}",
                $"回声填充={On(TogetherSettingsSync.EffectiveCompatEchoPopulateSkip)}",
                $"镜像单次={On(TogetherSettingsSync.EffectiveCompatMirroredPowerSingleFire)}",
                $"注能一次={On(TogetherSettingsSync.EffectiveCompatImbuedOnce)}",
                $"随机数预测={On(TogetherSettingsSync.EffectiveCompatRandomForeseerSync)}",
                "]");
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.GetType().Name}";
        }
    }

    private static string On(bool value)
    {
        return value ? "开" : "关";
    }
}
