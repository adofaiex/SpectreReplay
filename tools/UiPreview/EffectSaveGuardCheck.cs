using System;
using System.Reflection;
using SpectreReplay.Features.EffectRemover;

namespace UiPreview;

// 链接真实保护生命周期代码；假边界只模拟 Harmony 的返回值及双账本，不复刻被测决策。
internal static class EffectSaveGuardCheck
{
    internal static int Run()
    {
        int failed = 0;
        Check("SG01 六个必需钩子返回null均拒绝交接并清理部分安装", () =>
        {
            for (int at = 1; at <= 6; at++)
            {
                Reset(true);
                EffectGuardHarness.NullAt = at;
                Expect(Throws(DisabledEffectSaveGuard.Install), "null patch accepted at " + at);
                Expect(EffectGuardHarness.Count(EffectGuardHarness.Owner) == 0, "partial patches remain at " + at);
                Expect(!Flag("installed") && !Flag("callbacksEnabled"), "failed install published active state");
            }
        });
        Check("SG02 非空返回但实际钩子缺失也拒绝交接", () =>
        {
            for (int at = 1; at <= 6; at++)
            {
                Reset(true);
                EffectGuardHarness.OmitAt = at;
                Expect(Throws(DisabledEffectSaveGuard.Install), "unregistered patch accepted at " + at);
                Expect(EffectGuardHarness.Count(EffectGuardHarness.Owner) == 0 && !Flag("installed"), "rollback incomplete");
            }
        });
        Check("SG03 单Harmony安装幂等，释放仅移除自己的六个钩子", () =>
        {
            Reset(false);
            SeedOtherOwner();
            DisabledEffectSaveGuard.Install();
            int calls = EffectGuardHarness.PatchCalls;
            DisabledEffectSaveGuard.Install();
            Expect(EffectGuardHarness.Count(EffectGuardHarness.Owner) == 6 && calls == EffectGuardHarness.PatchCalls,
                "duplicate or missing native hooks");
            DisabledEffectSaveGuard.Release();
            Expect(EffectGuardHarness.Count(EffectGuardHarness.Owner) == 0 && EffectGuardHarness.Count("other-owner") == 1,
                "native release removed wrong owner");
            Expect(!Flag("installed") && !Flag("callbacksEnabled"), "native release retained active state");
        });
        Check("SG04 Patch写第二Harmony时，释放到真实账本且保留其它owner", () =>
        {
            Reset(true);
            SeedOtherOwner();
            DisabledEffectSaveGuard.Install();
            Expect(EffectGuardHarness.Local.Count == 0 && EffectGuardHarness.Count(EffectGuardHarness.Owner) == 6,
                "bridge scenario not exercised");
            DisabledEffectSaveGuard.Release();
            Expect(EffectGuardHarness.Count(EffectGuardHarness.Owner) == 0 && EffectGuardHarness.Count("other-owner") == 1,
                "bridge hooks remain or unrelated owner removed");
            Expect(!Flag("installed"), "bridge release retained installed state");
        });
        Check("SG05 两种Harmony卸载无效均拒绝成功并失能残留加载回调", () =>
        {
            foreach (bool bridge in new[] { false, true })
            {
                Reset(bridge);
                DisabledEffectSaveGuard.Install();
                EffectGuardHarness.IgnoreRelease = true;
                Expect(Throws(DisabledEffectSaveGuard.Release), "silent unpatch accepted");
                Expect(Flag("installed") && !Flag("callbacksEnabled"), "failed release forgot hooks or left callbacks enabled");
                var level = new ADOFAI.LevelData();
                var game = new scnGame { levelData = level };
                scnEditor.instance = new scnEditor { levelData = level };
                ADOBase.customLevel = game;
                EffectRemover.Safety.Track(level, "new-original");
                Callback("LoadedPostfix").Invoke(null, new object[] { game, "new.adofai", true });
                Expect(EffectRemover.Safety.IsStripped(level), "stale load callback removed new protection");
                Expect(Throws(DisabledEffectSaveGuard.Install), "reinstall stacked onto unreleased hooks");
                EffectGuardHarness.IgnoreRelease = false;
                DisabledEffectSaveGuard.Release();
                Expect(!Flag("installed") && EffectGuardHarness.Count(EffectGuardHarness.Owner) == 0,
                    "retry release did not recover");
                EffectRemover.Safety.MarkClean(level);
                scnEditor.instance = null; ADOBase.customLevel = null;
            }
        });
        Check("SG06 安装和回滚同时失败保留两个异常，修复卸载后可恢复", () =>
        {
            Reset(true);
            EffectGuardHarness.NullAt = 3;
            EffectGuardHarness.IgnoreRelease = true;
            Exception failure = null;
            try { DisabledEffectSaveGuard.Install(); } catch (Exception ex) { failure = ex; }
            Expect(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 2,
                "install or rollback failure lost");
            Expect(!Flag("callbacksEnabled"), "partial install enabled callbacks");
            EffectGuardHarness.IgnoreRelease = false;
            DisabledEffectSaveGuard.Release();
            Expect(EffectGuardHarness.Count(EffectGuardHarness.Owner) == 0, "partial bridge installation remains");
        });
        return failed;

        void Check(string label, Action scenario)
        {
            try { scenario(); Console.WriteLine("    ✓ " + label); }
            catch (Exception ex) { failed++; Console.WriteLine("    ✗ " + label + ": " + ex.Message); }
            finally
            {
                // 测试之间只清假账目，再让真实 Release 复位自身；不修改生产代码字段。
                Reset(false);
                scnEditor.instance = null; ADOBase.customLevel = null;
            }
        }
    }

    private static void Reset(bool bridge)
    {
        EffectGuardHarness.Reset(bridge);
        DisabledEffectSaveGuard.Release();
    }

    private static void SeedOtherOwner()
        => EffectGuardHarness.Patch("other-owner", typeof(scnEditor).GetMethod("SaveLevel"), Callback("SavePrefix"), null);

    private static MethodInfo Callback(string name)
        => typeof(DisabledEffectSaveGuard).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

    private static bool Flag(string name)
        => (bool)typeof(DisabledEffectSaveGuard).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private static bool Throws(Action action)
    {
        try { action(); return false; } catch { return true; }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
