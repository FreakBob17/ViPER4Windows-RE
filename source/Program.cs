using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ViperPc;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 4 && args[0] == "--route-watch")
            {
                if (!int.TryParse(args[1], out int owner) || owner == Environment.ProcessId) return 1;
                try { using var parent = System.Diagnostics.Process.GetProcessById(owner); parent.WaitForExit(); } catch (ArgumentException) { }
                try { new VirtualAudioRoute(args[2]).Restore(args[3]); return 0; }
                catch (Exception e) { try { File.WriteAllText(Path.Combine(args[2], "route-watch-error.txt"), e.ToString()); } catch { } return 1; }
            }
            if (args.Length >= 2 && args[0] == "--audio-devices") { File.WriteAllText(args[1], JsonSerializer.Serialize(WindowsAudio.Outputs(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (args.Length >= 2 && args[0] == "--audio-status") { File.WriteAllText(args[1], JsonSerializer.Serialize(new { Defaults = Enumerable.Range(0, 3).Select(WindowsAudio.DefaultId).ToArray(), Outputs = WindowsAudio.Outputs() }, new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (args.Length >= 5 && args[0] == "--route-test")
            {
                using var engine = new DspEngine(); using var host = new SystemAudioHost(); var route = new VirtualAudioRoute(args[3]);
                host.Start(args[1], args[2], engine.Process);
                try { route.Start(args[1], args[2]); route.Watch(Environment.ProcessPath!); File.WriteAllText(args[4], JsonSerializer.Serialize(new { Started = true, Pid = Environment.ProcessId })); while (!File.Exists(args[4] + ".stop")) Thread.Sleep(100); }
                finally { route.Restore(); host.Stop(); } return 0;
            }
            if (args.Length >= 4 && args[0] == "--virtual-prepare") return DriverCommand(args[3], () => { new VirtualAudioRoute(args[2]).Prepare(args[1]); return "Устройство Windows переименовано в ViPER"; });
            if (args.Length >= 2 && args[0] == "--driver-diagnose")
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(DriverSetup.Inspect(CoreAudioProbe.DefaultOutput()), new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length >= 4 && args[0] == "--driver-register") return DriverCommand(args[3], () => DriverSetup.Register(args[1], args[2]));
            if (args.Length >= 4 && args[0] == "--driver-unregister") return DriverCommand(args[3], () => DriverSetup.Unregister(args[1], args[2]));
            if (args.Length >= 3 && args[0] == "--driver-restore") return DriverCommand(args[2], () => DriverSetup.Restore(args[1]));
            if (args.Length >= 2 && args[0] == "--wait-parent" && int.TryParse(args[1], out var parentId)) { try { System.Diagnostics.Process.GetProcessById(parentId).WaitForExit(10000); } catch (ArgumentException) { } }
            if (args.Length > 0 && args[0] == "--self-test") return Verification.Run(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "verification"));
            if (args.Length >= 3 && args[0] == "--process")
            {
                if (Path.GetFullPath(args[1]).Equals(Path.GetFullPath(args[2]), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Входной и выходной WAV должны быть разными файлами.");
                var catalog = new FeatureCatalog(); var state = new SettingsState(); state.Reset(catalog);
                if (args.Length > 3) state.Import(args[3], catalog);
                state.Set("36868", true);
                var wave = WaveFile.Read(args[1]); using var engine = new DspEngine((uint)wave.SampleRate);
                ParameterMapping.Apply(engine, state);
                WaveFile.Process(args[1], args[2], engine.Process, engine.MasterEnabled ? (int)engine.LatencyFrames : 0);
                return 0;
            }
            bool first = true;
            bool previewOnly = args.Length > 0 && (args[0] == "--screenshot" || args[0] == "--ui-test");
            using var single = previewOnly ? null : new Mutex(true, "ViPER4Windows_RE_Controller", out first);
            if (!first) { MessageBox.Show("ViPER4Windows-RE уже запущен. Откройте окно через значок в трее."); return 0; }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "ViPER4Windows-RE", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
            var window = new MainWindow(args.Contains("--driver-page"), previewOnly);
            if (args.Contains("--connect-apo")) window.Loaded += (_, _) => window.TryConnectApo();
            if (args.Contains("--start-system")) window.Loaded += (_, _) => window.TryStartSystemAudio();
            if (args.Length >= 2 && args[0] == "--ui-test")
            {
                window.ShowActivated = false; window.Show();
                window.Dispatcher.InvokeAsync(() => { try { window.RunUiSmokeTest(args[1]); } catch (Exception e) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "ui-error.txt"), e.ToString()); } finally { window.Quit(); } }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            if (args.Length >= 2 && args[0] == "--screenshot")
            {
                window.ShowActivated = false; window.Show();
                window.Dispatcher.InvokeAsync(() =>
                {
                    window.UpdateLayout();
                    var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(window);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
                    using var stream = File.Create(args[1]); png.Save(stream);
                    window.Quit();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            return app.Run(window);
        }
        catch (Exception e)
        {
            try { Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "data")); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "data", "last-error.txt"), e.ToString()); } catch { }
            if (!args.Contains("--self-test")) MessageBox.Show(e.Message, "ViPER4Windows-RE", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    static int DriverCommand(string receiptPath, Func<string> action)
    {
        try
        {
            string result = action();
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new { Success = true, Result = result }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new { Success = false, Error = e.Message, Details = e.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
