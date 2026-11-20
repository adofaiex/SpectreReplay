// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using ADOFAI;
using System.Text;

namespace SpectreReplay.Features.EffectRemover;

// 只有摘事件随功能开关卸载：去特效后的数据及其历史副本可能还在编辑器里。
internal static class Patch_LevelDataDecode
{
    [SpectrePatch(typeof(ADOFAI.LevelData), "Decode", PatchType.Postfix, Feature = FeatureKeys.EffectRemover)]
    internal static void Postfix(ADOFAI.LevelData __instance) => EffectRemover.OnLevelDataDecode(__instance);
}

internal static class Patch_LevelDataCopySafety
{
    // Undo/Redo 历史就是 LevelData.Copy 的结果；在真实副本创建点复制保护，避免猜测历史栈尾。
    [SpectrePatch(typeof(ADOFAI.LevelData), "Copy", PatchType.Postfix)]
    internal static void Postfix(ADOFAI.LevelData __instance, ADOFAI.LevelData __result)
        => EffectRemover.Safety.Copy(__instance, __result);
}

internal static class Patch_EditorLoadGameScene
{
    [SpectrePatch(typeof(scnEditor), "LoadGameScene", PatchType.Postfix)]
    internal static void Postfix(scnEditor __instance) => EffectRemover.OnEditorLoadGameScenePostfix(__instance);
}

internal static class Patch_EffectDataSafety
{
    // Save As 修改 levelPath 后直接调用这里，必须在共同入口按实际目标路径保护。
    [SpectrePatch(typeof(scnEditor), "SaveLevel", PatchType.Prefix, TryingCatch = false)]
    internal static bool SavePrefix(scnEditor __instance) => EffectRemover.OnSaveLevelPrefix(__instance);

    [SpectrePatch(typeof(scnEditor), "SaveLevel", PatchType.Postfix)]
    internal static void SavedPostfix(scnEditor __instance) => EffectRemover.RefreshEditorSaveButtons(__instance);

    // Level export and Steam publishing also write the current level file directly.
    // Guard RDFile's final text write so every such path uses the same target-path policy.
    [SpectrePatch(typeof(RDFile), "WriteAllText", PatchType.Prefix, ParameterTypes = new[] { typeof(string), typeof(string), typeof(Encoding) })]
    internal static bool FileWritePrefix(string path) => EffectRemover.OnFileWritePrefix(path);

    [SpectrePatch(typeof(scnGame), "LoadLevel", PatchType.Postfix)]
    internal static void LoadedPostfix(scnGame __instance, string levelPath, bool __result)
        => EffectRemover.OnLevelLoaded(__instance.levelData, levelPath, __result);

    [SpectrePatch(typeof(scnEditor), "UndoOrRedo", PatchType.Postfix)]
    internal static void HistoryPostfix(scnEditor __instance) => EffectRemover.RefreshEditorSaveButtons(__instance);
}
