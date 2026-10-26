using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class GameClosureTests
{
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    static SteamGames.Item Item(int id, int parent, long start, string path)
    { return new SteamGames.Item { Id = id, Parent = parent, Started = start, Path = path }; }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fixture")
        {
            if (args.Length > 1) Process.Start(new ProcessStartInfo(args[1], "--fixture") { UseShellExecute = false, CreateNoWindow = true });
            Thread.Sleep(Timeout.Infinite); return 0;
        }
        Process parent = null, unrelated = null, child = null;
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "SteamStudyLock-Fixture-" + Guid.NewGuid().ToString("N"));
            string gameDir = Path.Combine(folder, @"steamapps\common\FixtureGame");
            string outside = Path.Combine(folder, "Outside");
            Directory.CreateDirectory(gameDir); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(folder, @"steamapps\appmanifest_1.acf"), "\"AppState\" { \"installdir\" \"FixtureGame\" }");
            string gameExe = Path.Combine(gameDir, "Fixture.exe"), childExe = Path.Combine(outside, "FixtureChild.exe");
            string self = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameFixture.exe");
            File.Copy(self, gameExe); File.Copy(self, childExe);
            var roots = SteamGames.RootsFromLibraries(new[] { folder });
            Require(roots.Count == 1, "Library manifest not discovered");
            Require(!SteamGames.Under(gameDir + "Other\\unrelated.exe", gameDir), "Sibling path incorrectly matched");
            var known = new Dictionary<int, SteamGames.Item>();
            var fakeRoot = Item(100, 1, 10, Path.Combine(gameDir, "game.exe"));
            var fakeChild = Item(101, 100, 11, childExe);
            var selected = SteamGames.Select(new List<SteamGames.Item> { fakeRoot, fakeChild }, roots, known);
            Require(selected.Count == 2, "External launcher child missed");
            selected = SteamGames.Select(new List<SteamGames.Item> { fakeChild }, roots, known);
            Require(selected.Count == 1, "Child lost after launcher exited");
            selected = SteamGames.Select(new List<SteamGames.Item> { Item(101, 1, 99, childExe) }, roots, known);
            Require(selected.Count == 0, "Reused PID inherited game ownership");
            known.Clear();
            selected = SteamGames.Select(new List<SteamGames.Item> { fakeRoot, Item(102, 100, 9, childExe) }, roots, known);
            Require(selected.Count == 1, "Older process attached to recycled parent PID");

            // Integration: actual disposable processes, never the user's Steam.
            parent = Process.Start(new ProcessStartInfo(gameExe, "--fixture \"" + childExe + "\"") { UseShellExecute = false, CreateNoWindow = true });
            unrelated = Process.Start(new ProcessStartInfo(childExe, "--fixture") { UseShellExecute = false, CreateNoWindow = true });
            known.Clear(); selected = null;
            for (int i = 0; i < 50; i++)
            {
                selected = SteamGames.Select(SteamGames.Snapshot(), roots, known);
                var childInfo = selected.Find(delegate(SteamGames.Item p) { return p.Parent == parent.Id; });
                if (childInfo != null) { child = Process.GetProcessById(childInfo.Id); break; }
                Thread.Sleep(100);
            }
            if (child == null || selected.Count != 2)
                foreach (var item in selected) Console.WriteLine("Selected fixture: {0} parent={1} {2}", item.Id, item.Parent, item.Path);
            Require(child != null && selected.Count == 2, "Real game tree not found; selected=" + selected.Count);
            Require(!selected.Exists(delegate(SteamGames.Item p) { return p.Id == unrelated.Id; }), "Unrelated process selected");
            for (int i = 0; i < 50 && !File.Exists(Path.Combine(outside, "ready-" + child.Id + ".txt")); i++) Thread.Sleep(100);
            string errorFile = Path.Combine(outside, "error-" + child.Id + ".txt");
            if (File.Exists(errorFile)) throw new Exception(File.ReadAllText(errorFile));
            Require(File.Exists(Path.Combine(outside, "ready-" + child.Id + ".txt")), "Fixture UI did not become ready");
            parent.Kill(); parent.WaitForExit(3000);
            selected = SteamGames.Select(SteamGames.Snapshot(), roots, known);
            if (selected.Count != 1 || selected[0].Id != child.Id)
            {
                Console.WriteLine("After parent exit: childExited={0}, selected={1}", child.HasExited, selected.Count);
                foreach (var item in selected) Console.WriteLine("Remaining: {0} parent={1} {2}", item.Id, item.Parent, item.Path);
            }
            Require(selected.Count == 1 && selected[0].Id == child.Id, "Detached real child was lost");
            // Wait until the fixture's message loop is ready to receive WM_CLOSE.
            var sent = new HashSet<string>();
            string hold = Path.Combine(outside, "hold-" + child.Id + ".txt");
            File.WriteAllText(hold, "Simulate waiting for save confirmation");
            SteamGames.RequestCloseSelected(selected, sent);
            Thread.Sleep(300);
            Require(!child.HasExited, "Game refusing exit was force-terminated");
            Require(!File.Exists(Path.Combine(outside, "saved-" + child.Id + ".txt")), "Save confirmation bypassed");
            File.Delete(hold); sent.Clear();
            for (int i = 0; i < 50 && sent.Count == 0; i++)
            { SteamGames.RequestCloseSelected(selected, sent); if (sent.Count == 0) Thread.Sleep(100); }
            if (sent.Count == 0)
            {
                using (var live = Process.GetProcessById(child.Id))
                    Console.WriteLine("No close request: hwnd={0} ticks={1} expected={2}", live.MainWindowHandle, live.StartTime.ToUniversalTime().Ticks, selected[0].Started);
            }
            Require(child.WaitForExit(3000), "Fixture child survived shutdown");
            Require(File.Exists(Path.Combine(outside, "saved-" + child.Id + ".txt")), "Normal-save handler did not complete before game exit");
            Require(!unrelated.HasExited, "Unrelated fixture was closed");
            Console.WriteLine("PASS: game library, path boundary, detached child, PID reuse, refusing exit preserved, normal-save handler before exit, unrelated process preserved.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (var p in new[] { parent, child, unrelated })
                if (p != null) { try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { } finally { p.Dispose(); } }
        }
    }
}
