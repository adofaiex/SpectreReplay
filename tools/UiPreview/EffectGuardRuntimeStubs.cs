// 只为链接真实 DisabledEffectSaveGuard 提供游戏/Harmony 边界；不修改真实方法或创建 detour。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace ADOFAI
{
    internal sealed class LevelData { public LevelData Copy() => new LevelData(); }
}

internal sealed class scnEditor
{
    internal static scnEditor instance;
    internal ADOFAI.LevelData levelData;
    public void SaveLevel() { }
    public void LoadGameScene() { }
    public void UndoOrRedo() { }
}

internal sealed class scnGame
{
    internal ADOFAI.LevelData levelData;
    public bool LoadLevel(string levelPath) => true;
}

internal static class ADOBase { internal static scnGame customLevel; }
internal static class RDFile { public static void WriteAllText(string path, string text, Encoding encoding) { } }

namespace SpectreReplay.Features.EffectRemover
{
    internal static class EffectRemover
    {
        internal static readonly EffectDataSafety Safety = new EffectDataSafety();
        internal static bool ProtectedHistory = true;
        internal static bool OnSaveLevelPrefix(scnEditor editor) => false;
        internal static bool OnFileWritePrefix(string path) => false;
        internal static void RefreshEditorSaveButtons(scnEditor editor) { }
        internal static bool HasProtectedEditorData() => ProtectedHistory;
    }
}

namespace HarmonyLib
{
    internal sealed class HarmonyMethod
    {
        internal readonly MethodInfo method;
        internal HarmonyMethod(MethodInfo method) { this.method = method; }
    }

    internal static class AccessTools
    {
        internal static MethodInfo Method(Type type, string name, Type[] parameters = null)
            => parameters == null
                ? type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                : type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                    null, parameters, null);
    }

    internal sealed class Harmony
    {
        private readonly string owner;
        public Harmony(string owner) { this.owner = owner; }
        internal MethodInfo Patch(MethodBase target, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
            => UiPreview.EffectGuardHarness.Patch(owner, target, prefix?.method, postfix?.method);
        public static object GetPatchInfo(MethodBase target) => UiPreview.EffectGuardHarness.ReadLocal(target);
        public void UnpatchAll(string owner) => UiPreview.EffectGuardHarness.UnpatchLocal(owner);
    }
}

namespace UiPreview
{
    // public：动态第二程序集通过正常的公共 CLR 调用访问同一测试夹具。
    public static class EffectGuardHarness
    {
        internal const string Owner = "com.spectre.replay.disabled-effect-save-guard";
        internal static readonly Dictionary<MethodBase, PatchLists> Local = new Dictionary<MethodBase, PatchLists>();
        internal static readonly Dictionary<MethodBase, PatchLists> Bridge = new Dictionary<MethodBase, PatchLists>();
        internal static bool BridgeMode, IgnoreRelease;
        internal static int NullAt, OmitAt, PatchCalls;
        private static bool bridgeCreated;

        internal sealed class PatchRecord
        {
            // 模拟真实 Harmony 的小写 owner 字段和 PatchMethod 属性。
            public readonly string owner;
            public MethodInfo PatchMethod { get; }
            internal PatchRecord(string owner, MethodInfo method) { this.owner = owner; PatchMethod = method; }
        }

        internal sealed class PatchLists
        {
            public readonly List<PatchRecord> Prefixes = new List<PatchRecord>();
            public readonly List<PatchRecord> Postfixes = new List<PatchRecord>();
            public readonly List<PatchRecord> Transpilers = new List<PatchRecord>();
            public readonly List<PatchRecord> Finalizers = new List<PatchRecord>();
        }

        internal static void Reset(bool bridge = false)
        {
            EnsureBridge();
            Local.Clear(); Bridge.Clear();
            BridgeMode = bridge; IgnoreRelease = false;
            NullAt = OmitAt = PatchCalls = 0;
            SpectreReplay.Features.EffectRemover.EffectRemover.ProtectedHistory = true;
        }

        internal static MethodInfo Patch(string owner, MethodBase target, MethodInfo prefix, MethodInfo postfix)
        {
            PatchCalls++;
            if (PatchCalls != OmitAt)
            {
                var ledger = BridgeMode ? Bridge : Local;
                if (!ledger.TryGetValue(target, out var lists)) ledger[target] = lists = new PatchLists();
                if (prefix != null) lists.Prefixes.Add(new PatchRecord(owner, prefix));
                if (postfix != null) lists.Postfixes.Add(new PatchRecord(owner, postfix));
            }
            // null 也可能留下部分账目；不能因账上有条目便把失败当成成功。
            return PatchCalls == NullAt ? null : prefix ?? postfix;
        }

        public static object ReadBridge(MethodBase target) => Bridge.TryGetValue(target, out var value) ? value : null;
        internal static object ReadLocal(MethodBase target) => Local.TryGetValue(target, out var value) ? value : null;
        public static void UnpatchBridge(string owner) { if (!IgnoreRelease) Remove(Bridge, owner); }
        internal static void UnpatchLocal(string owner) { if (!IgnoreRelease) Remove(Local, owner); }

        private static void Remove(Dictionary<MethodBase, PatchLists> ledger, string owner)
        {
            foreach (var lists in ledger.Values)
            {
                lists.Prefixes.RemoveAll(p => p.owner == owner);
                lists.Postfixes.RemoveAll(p => p.owner == owner);
                lists.Transpilers.RemoveAll(p => p.owner == owner);
                lists.Finalizers.RemoveAll(p => p.owner == owner);
            }
        }

        internal static int Count(string owner)
        {
            int count = 0;
            foreach (var ledger in new[] { Local, Bridge })
            foreach (var lists in ledger.Values)
            {
                count += lists.Prefixes.FindAll(p => p.owner == owner).Count;
                count += lists.Postfixes.FindAll(p => p.owner == owner).Count;
            }
            return count;
        }

        private static void EnsureBridge()
        {
            if (bridgeCreated) return;
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("EffectGuard.SecondHarmony"), AssemblyBuilderAccess.Run);
            TypeBuilder type = assembly.DefineDynamicModule("main").DefineType("HarmonyLib.Harmony", TypeAttributes.Public);
            var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string) });
            var il = constructor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)); il.Emit(OpCodes.Ret);
            var reader = type.DefineMethod("GetPatchInfo", MethodAttributes.Public | MethodAttributes.Static,
                typeof(object), new[] { typeof(MethodBase) });
            il = reader.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(EffectGuardHarness).GetMethod(nameof(ReadBridge))); il.Emit(OpCodes.Ret);
            var unpatch = type.DefineMethod("UnpatchAll", MethodAttributes.Public, typeof(void), new[] { typeof(string) });
            il = unpatch.GetILGenerator();
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, typeof(EffectGuardHarness).GetMethod(nameof(UnpatchBridge))); il.Emit(OpCodes.Ret);
            type.CreateType();
            bridgeCreated = true;
        }
    }
}
