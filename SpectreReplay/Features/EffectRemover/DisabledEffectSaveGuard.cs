using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ADOFAI;
using HarmonyLib;

namespace SpectreReplay.Features.EffectRemover;

// 普通补丁卸载前交接给独立 owner。它只维护保存安全，不再摘事件。
// 保护按数据对象及来源路径查询；干净对象可保存，历史里仍有剥离副本则保留钩子。
internal static class DisabledEffectSaveGuard
{
    private const string Owner = "com.spectre.replay.disabled-effect-save-guard";
    private static readonly Harmony Harmony = new Harmony(Owner);
    private static bool installed;
    private static bool callbacksEnabled;

    private readonly struct Hook
    {
        internal readonly MethodBase Target;
        internal readonly MethodInfo Patch;
        internal readonly bool Prefix;

        internal Hook(MethodBase target, string patch, bool prefix = false)
        {
            Target = target ?? throw new MissingMethodException("effect save guard target unavailable: " + patch);
            Patch = AccessTools.Method(typeof(DisabledEffectSaveGuard), patch)
                ?? throw new MissingMethodException("effect save guard callback unavailable: " + patch);
            Prefix = prefix;
        }
    }

    private static Hook[] Hooks() => new[]
    {
        new Hook(AccessTools.Method(typeof(scnEditor), "SaveLevel"), nameof(SavePrefix), true),
        new Hook(AccessTools.Method(typeof(RDFile), "WriteAllText", new[] { typeof(string), typeof(string), typeof(System.Text.Encoding) }), nameof(FileWritePrefix), true),
        new Hook(AccessTools.Method(typeof(scnGame), "LoadLevel"), nameof(LoadedPostfix)),
        new Hook(AccessTools.Method(typeof(LevelData), "Copy"), nameof(CopyPostfix)),
        new Hook(AccessTools.Method(typeof(scnEditor), "LoadGameScene"), nameof(EditorStatePostfix)),
        new Hook(AccessTools.Method(typeof(scnEditor), "UndoOrRedo"), nameof(EditorStatePostfix))
    };

    internal static void Install()
    {
        if (installed && callbacksEnabled) return;
        // 上次部分安装或释放失败的钩子不能叠加。清不干净就拒绝新交接。
        Release();
        try
        {
            foreach (Hook hook in Hooks())
            {
                var replacement = Harmony.Patch(hook.Target,
                    prefix: hook.Prefix ? new HarmonyMethod(hook.Patch) : null,
                    postfix: hook.Prefix ? null : new HarmonyMethod(hook.Patch));
                // ProxyPatch 可以返回 null 而不抛异常；返回非空也须确认实际账上有本钩子。
                if (replacement == null)
                    throw new InvalidOperationException("effect save guard patch returned null: " + hook.Target.Name);
                if (!IsApplied(hook))
                    throw new InvalidOperationException("effect save guard patch not registered: " + hook.Target.Name);
            }
            installed = true;
            callbacksEnabled = true;
        }
        catch (Exception installFailure)
        {
            try { Release(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("effect save guard install and rollback failed", installFailure, cleanupFailure);
            }
            throw;
        }
    }

    internal static void Release()
    {
        // 即使 detour 拆除失败，旧 LoadedPostfix 也不能清掉重新启用后建立的新保护。
        callbacksEnabled = false;
        Hook[] hooks = Hooks();
        var failures = new List<Exception>();
        foreach (Type harmonyType in HarmonyTypes())
        {
            try
            {
                ReleaseFrom(harmonyType, hooks);
                foreach (Hook hook in hooks)
                    if (ContainsOwnedPatch(harmonyType, hook.Target))
                        throw new InvalidOperationException("effect save guard still registered: " + hook.Target.Name);
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count != 0)
            throw new AggregateException("effect save guard release failed", failures);
        installed = false;
    }

    // UMMBridge 只改写 Patch、不改写 Unpatch。逐本实际账检查这个专用 owner，
    // 不按程序集名称猜测 HarmonyX，也不把另一套 Harmony 的强类型对象强转过来。
    private static IEnumerable<Type> HarmonyTypes()
    {
        yield return typeof(Harmony);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly == typeof(Harmony).Assembly) continue;
            Type type = assembly.GetType("HarmonyLib.Harmony");
            if (type?.GetMethod("GetPatchInfo", new[] { typeof(MethodBase) }) != null)
                yield return type;
        }
    }

    private static bool IsApplied(Hook hook)
    {
        foreach (Type harmonyType in HarmonyTypes())
            if (ContainsOwnedPatch(harmonyType, hook.Target, hook.Patch, hook.Prefix ? "Prefixes" : "Postfixes"))
                return true;
        return false;
    }

    private static bool ContainsOwnedPatch(Type harmonyType, MethodBase target,
        MethodInfo method = null, string listName = null)
    {
        MethodInfo reader = harmonyType.GetMethod("GetPatchInfo", new[] { typeof(MethodBase) })
            ?? throw new MissingMethodException(harmonyType.FullName, "GetPatchInfo");
        object patches = reader.Invoke(null, new object[] { target });
        if (patches == null) return false;
        foreach (string name in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
        {
            if (listName != null && name != listName) continue;
            object value = Member(patches, name);
            if (!(value is IEnumerable entries))
                throw new InvalidOperationException("effect save guard unreadable patch list: " + name);
            foreach (object entry in entries)
            {
                string owner = (Member(entry, "owner") ?? Member(entry, "Owner")) as string;
                if (owner == null) throw new InvalidOperationException("effect save guard unreadable patch owner");
                if (owner == Owner && (method == null || Equals(Member(entry, "PatchMethod"), method))) return true;
            }
        }
        return false;
    }

    private static object Member(object instance, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return instance.GetType().GetProperty(name, flags)?.GetValue(instance)
            ?? instance.GetType().GetField(name, flags)?.GetValue(instance);
    }

    private static void ReleaseFrom(Type harmonyType, Hook[] hooks)
    {
        bool found = false;
        foreach (Hook hook in hooks) found |= ContainsOwnedPatch(harmonyType, hook.Target);
        if (!found) return;
        MethodInfo unpatch = harmonyType.GetMethod("UnpatchAll", new[] { typeof(string) })
            ?? throw new MissingMethodException(harmonyType.FullName, "UnpatchAll");
        object harmony = Activator.CreateInstance(harmonyType, new object[] { Owner });
        unpatch.Invoke(harmony, new object[] { Owner });
    }

    private static bool SavePrefix(scnEditor __instance)
    {
        if (!callbacksEnabled) return true;
        // 不因当前对象干净而清除其它历史副本的标记。
        return EffectRemover.OnSaveLevelPrefix(__instance);
    }

    private static bool FileWritePrefix(string path)
        => !callbacksEnabled || EffectRemover.OnFileWritePrefix(path);

    private static void LoadedPostfix(scnGame __instance, string levelPath, bool __result)
    {
        if (!__result) return;
        if (!callbacksEnabled) return;
        var editor = scnEditor.instance;
        // OpenLevelCo 在 LoadLevel 前已清空历史，但成功后不走 LoadGameScene/UndoOrRedo。
        // editor.levelData 来自 customLevel；只在加载的确是当前编辑器关卡时交接收尾。
        if (editor == null || !ReferenceEquals(__instance, ADOBase.customLevel)) return;
        EffectRemover.Safety.MarkClean(__instance.levelData);
        EditorStatePostfix(editor);
    }

    private static void CopyPostfix(LevelData __instance, LevelData __result)
    {
        if (!callbacksEnabled) return;
        EffectRemover.Safety.Copy(__instance, __result);
    }

    private static void EditorStatePostfix(scnEditor __instance)
    {
        if (!callbacksEnabled) return;
        EffectRemover.RefreshEditorSaveButtons(__instance);
        if (!EffectRemover.HasProtectedEditorData()) Release();
    }
}
