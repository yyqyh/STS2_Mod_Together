using System.Reflection;
using System.Reflection.Emit;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Together.Core.Utils;

/// <summary>模型 / IL 的小工具（把散在各处的同一份实现收在一起）。</summary>
internal static class ModelAccess
{
    private static readonly Dictionary<(Type Type, string Name), FieldInfo?> FieldCache = [];

    private static readonly Dictionary<(Type Type, string Name), MethodInfo?> MethodCache = [];

    private static readonly Dictionary<Type, FieldInfo[]> FieldsByType = [];

    /// <summary>读一个私有字段（带缓存）；读不到返回 <c>null</c>，由调用方决定要不要告警。</summary>
    /// <remarks>键必须带<b>字段名</b>：同一类型上常要读好几个字段，只用类型当键会拿回上一个 <c>FieldInfo</c>。</remarks>
    public static FieldInfo? FieldOf(Type type, string name)
    {
        var key = (type, name);
        if (FieldCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        FieldInfo? field;
        try
        {
            field = AccessTools.Field(type, name);
        }
        catch (Exception)
        {
            field = null;
        }

        FieldCache[key] = field;
        return field;
    }

    /// <summary>找一个方法（带缓存）；<paramref name="parameters" /> 为空时按名字找（同名重载多的话要给参数类型）。</summary>
    public static MethodInfo? MethodOf(Type type, string name, params Type[] parameters)
    {
        var key = (type, name);
        if (MethodCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        MethodInfo? method;
        try
        {
            method = parameters.Length == 0
                ? AccessTools.Method(type, name)
                : AccessTools.Method(type, name, parameters);
        }
        catch (Exception)
        {
            method = null;
        }

        MethodCache[key] = method;
        return method;
    }

    /// <summary>某个类型上的全部实例字段（带缓存，含非公开）。</summary>
    public static FieldInfo[] FieldsOf(Type type)
    {
        if (FieldsByType.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        FieldsByType[type] = fields;
        return fields;
    }

    /// <summary>卡牌的 owner。<c>Owner</c> 的 getter 会 <c>AssertMutable</c>，对 canonical 模型会抛，所以兜一层。</summary>
    public static Player? OwnerOf(CardModel card)
    {
        try
        {
            return card.Owner;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>卡牌当前所在的堆（同上，兜 <c>Pile</c> 的抛）。</summary>
    public static CardPile? PileOf(CardModel card)
    {
        try
        {
            return card.Pile;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>这条方法的 IL 里有没有目标字符串常量。</summary>
    /// <remarks>
    /// 直接扫原始字节里的 <c>ldstr</c>（0x72）再用 <c>ResolveString</c> 取字符串，<b>不解析成指令序列</b>——
    /// 兼容层要扫几千个方法，解析指令会明显拖慢启动。0x72 也可能只是别的指令的操作数字节，
    /// 那种情况 <c>ResolveString</c> 会抛，吞掉即可（最坏多解析一次，不影响正确性）。
    /// </remarks>
    public static bool ContainsStringConstant(MethodBase method, string fragment)
    {
        try
        {
            var bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes is null)
            {
                return false;
            }

            for (var i = 0; i + 4 < bytes.Length; i++)
            {
                if (bytes[i] != OpCodes.Ldstr.Value)
                {
                    continue;
                }

                var token = BitConverter.ToInt32(bytes, i + 1);
                try
                {
                    var text = method.Module.ResolveString(token);
                    if (!string.IsNullOrEmpty(text)
                        && text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 这个 token 不是字符串，继续扫。
                }
            }
        }
        catch (Exception)
        {
            // 动态方法 / 没有方法体 / 元数据读不出来 —— 都当"没有"。
        }

        return false;
    }
}
