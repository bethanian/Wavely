using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;
using Windows.Storage.Pickers;
using Wavely.Core;

namespace Wavely.App;

public sealed partial class MainWindow
{
    private async void OnSettings(object Sender, RoutedEventArgs Args)
    {
        if (Busy || Engine.State != RecordingState.Idle)
        {
            ShowNotice("Finish recording first", "Device and quality changes apply to your next recording.", InfoBarSeverity.Informational);
            return;
        }
        await RunAsync(async () =>
        {
            var Microphones = new[] { new AudioDevice("", "Windows default microphone") }.Concat(RecordingEngine.Enumerate(DataFlow.Capture)).ToArray();
            var Outputs = new[] { new AudioDevice("", "Windows default output") }.Concat(RecordingEngine.Enumerate(DataFlow.Render)).ToArray();
            var Microphone = new ComboBox { Header = "Microphone", ItemsSource = Microphones, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedItem = Microphones.FirstOrDefault(Device => Device.Id == Settings.MicrophoneId) ?? Microphones[0] };
            var Output = new ComboBox { Header = "System audio / playback device", ItemsSource = Outputs, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedItem = Outputs.FirstOrDefault(Device => Device.Id == Settings.OutputId) ?? Outputs[0] };
            var Rate = new ComboBox { Header = "WAV quality · 16-bit stereo", ItemsSource = new[] { "44.1 kHz", "48 kHz" }, SelectedIndex = Settings.SampleRate == 44100 ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            var Bitrate = new ComboBox { Header = "MP3 export quality", ItemsSource = new[] { "128 kbps", "192 kbps", "320 kbps" }, SelectedIndex = Settings.Mp3Bitrate == 128000 ? 0 : Settings.Mp3Bitrate == 320000 ? 2 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            var Theme = new ComboBox { Header = "Appearance", ItemsSource = new[] { "Default", "Light", "Dark" }, SelectedItem = Settings.Theme, HorizontalAlignment = HorizontalAlignment.Stretch };
            var Folder = new TextBox { Header = "Save recordings to", Text = Settings.SaveDirectory };
            var Browse = new Button { Content = "Choose folder…" };
            Browse.Click += async (_, _) => await RunAsync(async () =>
            {
                var Picker = new FolderPicker(); Picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(Picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var Chosen = await Picker.PickSingleFolderAsync();
                if (Chosen != null) Folder.Text = Chosen.Path;
            });
            var Hotkey = new ToggleSwitch { Header = "Global recording shortcut", IsOn = Settings.HotkeyEnabled };
            var Modifiers = new ComboBox { Header = "Shortcut modifiers", ItemsSource = new[] { "Ctrl + Shift", "Ctrl + Alt", "Alt + Shift" }, SelectedIndex = Settings.HotkeyModifiers == 3 ? 1 : Settings.HotkeyModifiers == 5 ? 2 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var Key = new ComboBox { Header = "Shortcut key", ItemsSource = Enumerable.Range('A', 26).Select(Value => ((char)Value).ToString()).ToArray(), SelectedIndex = (int)Math.Clamp(Settings.HotkeyKey - 'A', 0, 25), HorizontalAlignment = HorizontalAlignment.Stretch };
            var Tray = new ToggleSwitch { Header = "Keep in tray when minimized", IsOn = Settings.MinimizeToTray };
            var Recovery = new Button { Content = "Recover recordings to current save folder" };
            Recovery.Click += async (_, _) => await RunAsync(async () =>
            {
                if (!Directory.Exists(AppPaths.Sessions)) { ShowNotice("No interrupted sessions", "All recordings are finalized.", InfoBarSeverity.Informational); return; }
                var Count = 0;
                foreach (var Metadata in Directory.GetFiles(AppPaths.Sessions, "*.json"))
                {
                    if (!File.Exists(Path.ChangeExtension(Metadata, ".pcm"))) continue;
                    await Task.Run(() => SessionJournal.Recover(Metadata, Settings.SaveDirectory)); Count++;
                }
                await RefreshAsync(); ShowNotice("Recovery complete", $"Restored {Count} recording(s).", InfoBarSeverity.Success);
            });
            var Panel = new StackPanel { Spacing = 16, Width = 420 };
            foreach (var Control in new UIElement[] { Microphone, Output, Rate, Bitrate, Folder, Browse, Theme, Hotkey, Modifiers, Key, Tray, Recovery, new TextBlock { Text = "Ctrl+R  Record / stop\nSpace  Pause / resume or play\nF2  Rename selected recording\n\nOffline by design. No accounts or telemetry.\nWavely 1.0.1 · MIT License", TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12 } }) Panel.Children.Add(Control);
            var Dialog = new ContentDialog { Title = "Settings", Content = new ScrollViewer { Content = Panel, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            Dialog.PrimaryButtonClick += (_, Click) =>
            {
                try
                {
                    var Destination = Path.GetFullPath(Folder.Text.Trim());
                    Directory.CreateDirectory(Destination);
                    var Probe = Path.Combine(Destination, $".wavely-{Guid.NewGuid():N}.tmp");
                    using (File.Create(Probe)) { }
                    File.Delete(Probe);
                }
                catch (Exception Error) { Click.Cancel = true; Folder.Header = $"Cannot use folder: {Error.Message}"; }
            };
            if (await ShowDialogAsync(Dialog) != ContentDialogResult.Primary) return;
            if (!Settings.LibraryDirectories.Contains(Settings.SaveDirectory, StringComparer.OrdinalIgnoreCase)) Settings.LibraryDirectories.Add(Settings.SaveDirectory);
            Settings.MicrophoneId = (Microphone.SelectedItem as AudioDevice)?.Id;
            Settings.OutputId = (Output.SelectedItem as AudioDevice)?.Id;
            Settings.SampleRate = Rate.SelectedIndex == 0 ? 44100 : 48000;
            Settings.Mp3Bitrate = new[] { 128000, 192000, 320000 }[Bitrate.SelectedIndex];
            Settings.SaveDirectory = Path.GetFullPath(Folder.Text.Trim());
            Settings.Theme = Theme.SelectedItem as string ?? "Default";
            Settings.HotkeyEnabled = Hotkey.IsOn;
            Settings.HotkeyModifiers = Modifiers.SelectedIndex == 1 ? 3u : Modifiers.SelectedIndex == 2 ? 5u : 6u;
            Settings.HotkeyKey = (uint)('A' + Key.SelectedIndex);
            Settings.MinimizeToTray = Tray.IsOn;
            Store.Save(Settings); ApplySettings();
            if (Selected != null) await Playback.LoadAsync(Selected.Path, Settings.OutputId);
            await RefreshAsync();
        });
    }
}
