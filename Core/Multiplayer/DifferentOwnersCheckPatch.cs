using System.Reflection;
using System.Reflection.Emit;

using HarmonyLib;

using MegaCrit.Sts2.Core.Commands;

namespace Together.Core.Multiplayer;

/// <summary>
/// 方法 1（重做版）：打掉批量 <c>CardPileCmd.Add</c> 里那条"同批 owner 必须一致"的校验，
/// 让共享牌堆里的牌保持<b>自然归属</b>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须这么做：那条校验的前提是"每个玩家的牌堆各自独立"。共享牌库下共享的弃牌堆／抽牌堆
/// 本来就该同时装着两个人的牌，而<b>洗牌</b>正是"把弃牌堆混进抽牌堆"的批量 Add ——
/// 一旦抛 <c>…different owners…</c>，<b>回合循环直接终止、战斗卡住</b>。
/// </para>
/// <para>
/// 之前的临时办法是"把共享堆里牌的 owner 统一改成锚点"去迎合这条校验，但它连带出两个问题：
/// <list type="bullet">
/// <item><description><b>幽灵卡</b>：<c>CardModel.Pile</c> 按 owner 反查堆，owner 改了之后反查失效。</description></item>
/// <item><description><b>变牌失败</b>：<c>CardCmd.Transform</c> 要求替换卡与原卡 owner 一致，owner 不再唯一对应"牌属于谁"就撞上。</description></item>
/// </list>
/// 所以正确解法是让这条校验失效，owner 保持自然归属。
/// </para>
/// <para>
/// <b>关键点（上一版失败的原因）</b>：<c>Add</c> 是 <c>async</c> 方法，Harmony 的 transpiler
/// 默认打在<b>存根</b>上（只有"创建状态机"那几条指令），真实代码在编译器生成的
/// <c>CardPileCmd+&lt;Add&gt;d__N.MoveNext</c> 里。所以必须显式把目标指到状态机的 <c>MoveNext</c>。
/// （日志里能看到别的 mod 也在打 <c>&lt;Add&gt;d__10.MoveNext</c>，佐证了这一点。）
/// </para>
/// </remarks>
[HarmonyPatch]
internal static class DifferentOwnersCheckPatch
{
    /// <summary>错误信息的关键片段。刻意只匹配片段：本体不同位置的措辞不完全一致。</summary>
    private const string MessageFragment = "different owners";

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        foreach (var nested in typeof(CardPileCmd).GetNestedTypes(AccessTools.all))
        {
            if (!nested.Name.Contains("Add", StringComparison.Ordinal))
            {
                continue;
            }

            var moveNext = AccessTools.Method(nested, "MoveNext");
            if (moveNext is not null && ContainsFragment(moveNext))
            {
                return moveNext;
            }
        }

        throw new InvalidOperationException(
            $"在 CardPileCmd 的所有内嵌状态机里都没找到含 \"{MessageFragment}\" 的 MoveNext。" +
            "本体可能改过这条校验（或它不在状态机里），请重新核对后再启用本补丁。");
    }

    /// <summary>粗查：这条方法的 IL 里是否出现目标字符串（用于挑出正确的状态机）。</summary>
    private static bool ContainsFragment(MethodBase method)
    {
        try
        {
            var body = method.GetMethodBody();
            if (body is null)
            {
                return false;
            }

            foreach (var instruction in PatchProcessor.GetCurrentInstructions(method, out _)
                         ?? Enumerable.Empty<CodeInstruction>())
            {
                if (instruction.opcode == OpCodes.Ldstr
                    && instruction.operand is string text
                    && text.Contains(MessageFragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // 读不出 IL 就当没找到。
        }

        return false;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].opcode != OpCodes.Ldstr
                || list[i].operand is not string text
                || !text.Contains(MessageFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 往后找 newobj（构造异常对象）与其后的 throw，允许中间夹 nop 等填充指令。
            for (var j = i + 1; j < Math.Min(i + 8, list.Count); j++)
            {
                if (list[j].opcode != OpCodes.Newobj)
                {
                    continue;
                }

                // newobj 消耗刚 push 的字符串并留下异常对象；换成 pop 保持栈平衡。
                list[j] = new CodeInstruction(OpCodes.Pop);

                for (var k = j + 1; k < Math.Min(j + 4, list.Count); k++)
                {
                    if (list[k].opcode == OpCodes.Throw)
                    {
                        list[k] = new CodeInstruction(OpCodes.Nop);
                        return list;
                    }
                }
            }

            throw new InvalidOperationException(
                $"在 \"{text}\" 附近没找到 newobj + throw 的常规形状，无法安全改写这段 IL。");
        }

        throw new InvalidOperationException(
            $"MoveNext 里没找到含 \"{MessageFragment}\" 的字符串，无法改写。");
    }
}
