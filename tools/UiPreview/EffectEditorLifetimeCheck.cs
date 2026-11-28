using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// Execute the real scene-load/save entry points and safety policy; only Unity/game state is fake.
internal static class EffectEditorLifetimeCheck
{
    internal static int Run()
    {
        var type = CSharpSyntaxTree.ParseText(SrcUtil.Source(Driver.ProjectDir,
            "Features/EffectRemover/EffectRemover.cs")).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "EffectRemover");
        string[] methods = { "CanSave", "CurrentGame", "OnEditorLoadGameScenePostfix", "RefreshEditorSaveButtons",
            "SetEditorSaveButtons", "OnSaveLevelPrefix", "OnExportLevelPrefix", "OnFileWritePrefix" };
        string[] properties = { "CurrentLevelData", "EditorSaveEnabled", "Enabled" };
        string members = string.Join("\n", type.Members.Where(m =>
            m is MethodDeclarationSyntax method && methods.Contains(method.Identifier.ValueText)
            || m is PropertyDeclarationSyntax property && properties.Contains(property.Identifier.ValueText)
            || m is FieldDeclarationSyntax field && field.Declaration.Variables.Any(v =>
                v.Identifier.ValueText == "Safety" || v.Identifier.ValueText == "ModDisabled")));
        var patch = CSharpSyntaxTree.ParseText(SrcUtil.Source(Driver.ProjectDir,
            "Features/EffectRemover/Patch_EffectRemover.cs")).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "Patch_EditorLoadGameScene");
        string product = "using ADOFAI; using UnityEngine.SceneManagement; using SpectreReplay.PatchShared;\n"
            + "namespace SpectreReplay.Features.EffectRemover { internal static class EffectRemover {\n"
            + members + "\n}\n" + patch + "\n}";
        return RenderRepairCheck.RunFixture("EffectEditorLifetime", product,
            SrcUtil.Source(Driver.ProjectDir, "Features/EffectRemover/EffectDataSafety.cs"),
            SrcUtil.Source(Driver.ProjectDir, "Features/EffectRemover/LevelFileIdentity.cs"),
            SrcUtil.Source(Driver.ProjectDir, "Features/EffectRemover/SnapshotRestoreGuard.cs"), Boundary, Fixture);
    }

    private const string Boundary = """
using System;
namespace ADOFAI { public sealed class LevelData { } }
public sealed class scnGame
{
    public static scnGame instance;
    public ADOFAI.LevelData levelData = new();
    public string levelPath;
}
public static class ADOBase
{
    public static scnGame customLevel => scnGame.instance;
    public static string levelPath => scnGame.instance.levelPath;
}
public sealed class Button { public bool interactable = true; }
public sealed class scnEditor
{
    public static scnEditor instance;
    public scnGame customLevel;
    // Native r150 getter: this is NOT nullable-safe before Start assigns customLevel.
    public ADOFAI.LevelData levelData => customLevel.levelData;
    public Button popupUnsavedChangesSave = new(), buttonSave = new();
}
namespace UnityEngine
{
    public static class Debug { public static void LogWarning(object message) { } }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { public string name; }
    public static class SceneManager
    {
        public static Scene GetActiveScene() => new Scene { name = "scnEditor" };
    }
}
namespace SpectreReplay
{
    public sealed class Main
    {
        public static Main Instance = new();
        public bool Enabled = true;
    }
    public static class FeatureKeys { public const string EffectRemover = "effects"; }
    public static class FeatureGate { public static bool Enabled(string key) => true; }
    public static class Settings { public static bool EffectRemoverOn = true, EffectRemoverEnableSave; }
}
namespace SpectreReplay.PatchShared
{
    public enum PatchType { Postfix }
    public sealed class SpectrePatchAttribute : Attribute
    {
        public SpectrePatchAttribute(Type type, string method, PatchType patch) { }
    }
}
""";

    private const string Fixture = """
using System;
using System.IO;
using SpectreReplay;
using SpectreReplay.Features.EffectRemover;
public static class RenderRepairFixture_EffectEditorLifetime
{
    public static string[] Run()
    {
        var results = new System.Collections.Generic.List<string>();
        Check("EL01 LoadGameScene返回、Start前没有game时不读空customLevel", () =>
        {
            Reset();
            var editor = scnEditor.instance = new scnEditor { buttonSave = null, popupUnsavedChangesSave = null };
            Patch_EditorLoadGameScene.Postfix(editor);
            Expect(EffectRemover.EditorSaveEnabled, "uninitialized editor threw or blocked clean state");
            Expect(EffectRemover.OnFileWritePrefix("unused.adofai"), "no current game must be safe");
        });
        Check("EL02 Start前游戏单例已就绪时，按钮和所有写入口仍保护原谱", () =>
        {
            Reset();
            var game = ProtectedGame();
            scnGame.instance = game;
            var editor = scnEditor.instance = new scnEditor();
            Patch_EditorLoadGameScene.Postfix(editor);
            Expect(!editor.buttonSave.interactable && !editor.popupUnsavedChangesSave.interactable,
                "scene-load refresh lost original-file protection");
            Expect(!EffectRemover.EditorSaveEnabled && !EffectRemover.OnSaveLevelPrefix(editor)
                && !EffectRemover.OnExportLevelPrefix(editor) && !EffectRemover.OnFileWritePrefix(game.levelPath),
                "pre-Start save/export/direct-write bypassed safety");
        });
        Check("EL03 刷新传入编辑器按其实际谱面和路径判定，不借用另一单例", () =>
        {
            Reset();
            scnGame.instance = new scnGame();
            scnEditor.instance = new scnEditor { customLevel = scnGame.instance };
            var editor = new scnEditor { customLevel = ProtectedGame() };
            Patch_EditorLoadGameScene.Postfix(editor);
            Expect(!editor.buttonSave.interactable && !editor.popupUnsavedChangesSave.interactable,
                "refreshed buttons using another editor's clean data");
            Expect(!EffectRemover.OnSaveLevelPrefix(editor) && !EffectRemover.OnExportLevelPrefix(editor),
                "save/export used a different game's path");
        });
        Check("EL04 Start后仍阻止覆盖，显式允许、另存和干净谱面可恢复按钮", () =>
        {
            Reset();
            var game = scnGame.instance = ProtectedGame();
            var editor = scnEditor.instance = new scnEditor { customLevel = game };
            Patch_EditorLoadGameScene.Postfix(editor);
            Expect(!editor.buttonSave.interactable, "loaded protected original was writable");
            Settings.EffectRemoverEnableSave = true;
            EffectRemover.RefreshEditorSaveButtons();
            Expect(editor.buttonSave.interactable && editor.popupUnsavedChangesSave.interactable
                && EffectRemover.OnSaveLevelPrefix(editor), "explicit override did not work");
            Settings.EffectRemoverEnableSave = false;
            game.levelPath += ".new.adofai";
            EffectRemover.RefreshEditorSaveButtons();
            Expect(editor.buttonSave.interactable && EffectRemover.OnSaveLevelPrefix(editor), "Save As blocked");
            game.levelData = new ADOFAI.LevelData();
            EffectRemover.RefreshEditorSaveButtons();
            Expect(editor.buttonSave.interactable && EffectRemover.EditorSaveEnabled, "clean data remained blocked");
        });
        return results.ToArray();
        void Check(string label, Action test)
        {
            try { test(); results.Add("✓ " + label); }
            catch (Exception ex) { results.Add("✗ " + label + ": " + ex.Message); }
        }
    }
    private static void Reset()
    {
        scnEditor.instance = null;
        scnGame.instance = null;
        Settings.EffectRemoverEnableSave = false;
    }
    private static scnGame ProtectedGame()
    {
        var game = new scnGame { levelPath = Path.Combine(Path.GetTempPath(), "SpectreReplay-EL-" + Guid.NewGuid(), "original.adofai") };
        EffectRemover.Safety.Track(game.levelData, "original");
        EffectRemover.Safety.BindSource(game.levelData, game.levelPath);
        return game;
    }
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
""";
}
