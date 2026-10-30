using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Wavely.Core;

namespace Wavely.App;

public sealed partial class MainWindow : Window
{
    private readonly RecordingEngine Engine = new();
    private readonly PlaybackService Playback = new();
    private readonly SettingsStore Store = new();
    private readonly LibraryService Library = new();
    private readonly AppSettings Settings;
    private readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly NativeShell Shell;
    private IReadOnlyList<Recording> Recordings = [];
    private Recording? Selected;
    private bool Busy;
    private bool UpdatingSeek;
    private bool Closing;
    private bool DialogOpen;
    private int SelectionVersion;
    private bool Initialized;
    private bool RefreshingLibrary;
    private readonly Storyboard RecordingPulse = new();

    public MainWindow()
    {
        Settings = Store.Load();
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        Root.PreviewKeyDown += OnPreviewKeyDown;
        Root.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var Pulse = new DoubleAnimation { From = 1, To = 0.72, Duration = TimeSpan.FromMilliseconds(1100), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(Pulse, RecordIcon); Storyboard.SetTargetProperty(Pulse, "Opacity"); RecordingPulse.Children.Add(Pulse);
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1080, 760));
        if (AppWindow.Presenter is OverlappedPresenter Presenter) Presenter.PreferredMinimumWidth = 900;
        Shell = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Shell.Hotkey += () => DispatcherQueue.TryEnqueue(async () => await ToggleRecordingAsync());
        Shell.Restore += () => { AppWindow.Show(); Shell.Show(); Activate(); };
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += (_, Args) =>
        {
            if (Settings.MinimizeToTray && Args.DidPresenterChange && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) AppWindow.Hide();
        };
        Engine.Faulted += Error => DispatcherQueue.TryEnqueue(async () =>
        {
            await StopRecordingAsync();
            ShowNotice("Recording interrupted", $"{Error.Message} The captured portion was saved, or remains available for recovery.", InfoBarSeverity.Warning);
        });
        Playback.Faulted += Error => DispatcherQueue.TryEnqueue(() => ShowError(Error));
        SourceBox.SelectedIndex = (int)Settings.Source;
        ApplySettings();
        Timer.Tick += OnTick;
        Timer.Start();
        Initialized = true;
        Root.Loaded += async (_, _) => { await RefreshAsync(); await RecoverAsync(); };
    }

    private void ApplySettings()
    {
        Root.RequestedTheme = Settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyCaptionTheme();
        FormatText.Text = $"WAV · {Settings.SampleRate / 1000d:0.#} kHz · Stereo";
        Shell.SetTray(Settings.MinimizeToTray, Engine.State != RecordingState.Idle);
        if (!Shell.RegisterHotkey(Settings.HotkeyEnabled, Settings.HotkeyModifiers, Settings.HotkeyKey)) ShowNotice("Hotkey unavailable", "Another app is using this shortcut. Choose a different one in Settings.", InfoBarSeverity.Warning);
    }

    private void ApplyCaptionTheme()
    {
        var Light = Root.ActualTheme == ElementTheme.Light;
        AppWindow.TitleBar.ButtonForegroundColor = Light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Light ? Microsoft.UI.ColorHelper.FromArgb(255, 110, 110, 110) : Microsoft.UI.ColorHelper.FromArgb(255, 170, 170, 170);
        AppWindow.TitleBar.ButtonBackgroundColor = AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    private async Task RefreshAsync(string? SelectPath = null)
    {
        SelectPath ??= Selected?.Path;
        var Directories = Settings.LibraryDirectories.Append(Settings.SaveDirectory).ToArray();
        Recordings = await Task.Run(() => Library.Scan(Directories));
        RefreshingLibrary = true;
        try
        {
            FilterLibrary();
            if (SelectPath != null) LibraryList.SelectedItem = Recordings.FirstOrDefault(Item => Item.Path == SelectPath);
        }
        finally { RefreshingLibrary = false; }
        await LoadSelectionAsync();
    }
    private void FilterLibrary()
    {
        var Query = SearchBox.Text ?? "";
        var Items = Recordings.Where(Item => Item.Name.Contains(Query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        LibraryList.ItemsSource = Items;
        EmptyLibrary.Visibility = Items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void OnRefresh(object Sender, RoutedEventArgs Args) { if (!Busy && Engine.State == RecordingState.Idle) await RunAsync(() => RefreshAsync()); }
    private void OnSearchChanged(AutoSuggestBox Sender, AutoSuggestBoxTextChangedEventArgs Args) { if (Initialized) FilterLibrary(); }

    private async void OnSelectionChanged(object Sender, SelectionChangedEventArgs Args)
    {
        if (!RefreshingLibrary) await LoadSelectionAsync();
    }

    private async Task LoadSelectionAsync()
    {
        if (Busy || Engine.State != RecordingState.Idle) return;
        var Version = ++SelectionVersion;
        Selected = LibraryList.SelectedItem as Recording;
        var Item = Selected;
        Busy = true; UpdateControls();
        try
        {
            await Playback.DisposeAsync();
            if (Item == null) return;
            await Playback.LoadAsync(Item.Path, Settings.OutputId);
            var Peaks = await Task.Run(() => LibraryService.ReadPeaks(Item.Path));
            if (Version != SelectionVersion) return;
            RecordingTitle.Text = Item.Name;
            RecordingSubtitle.Text = $"{Item.Created:dddd, d MMMM yyyy} · WAV recording";
            Waveform.SetPeaks(Peaks);
            WaveformHint.Visibility = Visibility.Collapsed;
            SeekSlider.Maximum = Math.Max(0.001, Item.Duration.TotalSeconds);
            DurationText.Text = FormatTime(Item.Duration);
        }
        catch (Exception Error) { ShowError(Error); }
        finally { Busy = false; UpdateControls(); }
    }

    private async Task ToggleRecordingAsync()
    {
        if (Busy || DialogOpen) return;
        if (Engine.State != RecordingState.Idle) { await StopRecordingAsync(); return; }
        Busy = true;
        UpdateControls();
        try
        {
            await Playback.DisposeAsync();
            await Engine.StartAsync(Settings);
            Waveform.SetPeaks([]);
            WaveformHint.Visibility = Visibility.Collapsed;
            RecordingTitle.Text = "New recording";
            RecordingSubtitle.Text = Settings.Source switch { AudioSource.Microphone => "Capturing your microphone", AudioSource.SystemAudio => "Capturing system audio", _ => "Capturing microphone + system audio" };
            Notice.IsOpen = false;
            Shell.SetTray(Settings.MinimizeToTray, true);
        }
        catch (Exception Error) { ShowError(Error); }
        finally { Busy = false; UpdateControls(); if (Engine.State == RecordingState.Recording) RecordButton.Focus(FocusState.Programmatic); }
    }

    private async Task StopRecordingAsync(bool RefreshLibrary = true)
    {
        if (Busy || Engine.State == RecordingState.Idle) return;
        Busy = true;
        UpdateControls();
        string? PathName = null;
        try { PathName = await Engine.StopAsync(); }
        catch (Exception Error) { ShowNotice("Audio preserved for recovery", $"Saving could not finish: {Error.Message} Your session is kept locally. Use Recover recordings in Settings.", InfoBarSeverity.Error); AppPaths.Log(Error); }
        finally { Busy = false; Shell.SetTray(Settings.MinimizeToTray, false); UpdateControls(); }
        if (PathName != null && RefreshLibrary)
        {
            await RefreshAsync(PathName);
            if (Engine.LateFrames > 0) ShowNotice("Saved with timing gaps", $"{Engine.LateFrames} late audio frames could not be placed. The rest of the recording is saved.", InfoBarSeverity.Warning);
            else ShowNotice("Recording saved", Path.GetFileName(PathName), InfoBarSeverity.Success);
        }
    }

    private async void OnRecord(object Sender, RoutedEventArgs Args) => await ToggleRecordingAsync();
    private void OnPause(object Sender, RoutedEventArgs Args) { Engine.TogglePause(); UpdateControls(); }
    private void OnPlay(object Sender, RoutedEventArgs Args) { try { Playback.Toggle(); } catch (Exception Error) { ShowError(Error); } }
    private void OnSourceChanged(object Sender, SelectionChangedEventArgs Args)
    {
        if (!Initialized) return;
        Settings.Source = (AudioSource)SourceBox.SelectedIndex;
        try { Store.Save(Settings); } catch (Exception Error) { ShowError(Error); }
    }
    private void OnSeek(object Sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs Args)
    {
        if (!UpdatingSeek && Engine.State == RecordingState.Idle) { try { Playback.Seek(TimeSpan.FromSeconds(Args.NewValue)); } catch (Exception Error) { ShowError(Error); } }
    }

    private void OnTick(object? Sender, object Args)
    {
        var Active = Engine.State is RecordingState.Recording or RecordingState.Paused;
        Timer.Interval = TimeSpan.FromMilliseconds(Active || Playback.IsPlaying ? 50 : 250);
        var Time = Active ? Engine.Elapsed : Playback.Position;
        ElapsedText.Text = $"{(int)Time.TotalMinutes:00}:{Time.Seconds:00}.{Time.Milliseconds / 100}";
        LevelBar.Value = Engine.Peak;
        if (Engine.State == RecordingState.Recording) Waveform.Push(Engine.Peak);
        if (!Active)
        {
            UpdatingSeek = true;
            SeekSlider.Value = Playback.Position.TotalSeconds;
            UpdatingSeek = false;
            PositionText.Text = FormatTime(Playback.Position);
            Waveform.SetProgress(Playback.Duration.TotalSeconds > 0 ? Playback.Position / Playback.Duration : 0);
        }
        else { PositionText.Text = FormatTime(Engine.Elapsed); DurationText.Text = "REC"; }
        PlayIcon.Glyph = Playback.IsPlaying ? "\uE769" : "\uE768";
    }

    private void UpdateControls()
    {
        var Active = Engine.State is RecordingState.Recording or RecordingState.Paused;
        RecordButton.IsEnabled = !Busy;
        RecordIcon.Glyph = Active ? "\uE71A" : "\uE7C8";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RecordButton, Active ? "Stop and save recording" : "Start recording");
        PauseButton.IsEnabled = Active && !Busy;
        PauseIcon.Glyph = Engine.State == RecordingState.Paused ? "\uE768" : "\uE769";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PauseButton, Engine.State == RecordingState.Paused ? "Resume recording" : "Pause recording");
        SourceBox.IsEnabled = LibraryList.IsEnabled = SearchBox.IsEnabled = !Active && !Busy;
        PlayButton.IsEnabled = SeekSlider.IsEnabled = ActionsButton.IsEnabled = !Active && !Busy && Selected != null;
        StateText.Text = Busy ? "Saving your recording…" : Engine.State switch { RecordingState.Recording => "Recording · Space to pause", RecordingState.Paused => "Paused · Space to resume", _ => "Start recording · Ctrl+R" };
        if (Engine.State == RecordingState.Recording && RecordingPulse.Children.Count > 0) RecordingPulse.Begin();
        else { RecordingPulse.Stop(); RecordIcon.Opacity = 1; }
    }

    private void OnKeyDown(object Sender, KeyRoutedEventArgs Args)
    {
        if (DialogOpen || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or AutoSuggestBox) return;
        if (Args.Key == VirtualKey.F2 && Selected != null) { Args.Handled = true; OnRename(Sender, new()); }
    }

    private async void OnPreviewKeyDown(object Sender, KeyRoutedEventArgs Args)
    {
        if (DialogOpen || Busy) return;
        var Control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var Shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var Alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (Control && !Shift && !Alt && Args.Key == VirtualKey.R) { Args.Handled = true; await ToggleRecordingAsync(); return; }
        if (Args.Key != VirtualKey.Space) return;
        if (Engine.State is RecordingState.Recording or RecordingState.Paused)
        {
            Args.Handled = true; Engine.TogglePause(); UpdateControls();
        }
        else if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is not (TextBox or AutoSuggestBox or ComboBox))
        {
            Args.Handled = true;
            try { Playback.Toggle(); } catch (Exception Error) { ShowError(Error); }
        }
    }

    private async void OnRename(object Sender, RoutedEventArgs Args)
    {
        if (Selected is not { } Item) return;
        var Input = new TextBox { Text = Item.Name, MaxLength = 120, Header = "Recording name", MinWidth = 360 };
        var Result = await ShowDialogAsync(new ContentDialog { Title = "Rename recording", Content = Input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary });
        if (Result == ContentDialogResult.Primary) await RunAsync(async () => { await Playback.DisposeAsync(); await RefreshAsync(LibraryService.Rename(Item, Input.Text)); });
    }
    private async void OnDelete(object Sender, RoutedEventArgs Args)
    {
        if (Selected is not { } Item) return;
        var Result = await ShowDialogAsync(new ContentDialog { Title = "Delete recording?", Content = $"“{Item.Name}” will be permanently deleted.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel" });
        if (Result != ContentDialogResult.Primary) return;
        await RunAsync(async () =>
        {
            await Playback.DisposeAsync(); File.Delete(Item.Path); Selected = null;
            RecordingTitle.Text = "Ready when you are"; RecordingSubtitle.Text = "A little space for your next idea.";
            Waveform.SetPeaks([]); WaveformHint.Visibility = Visibility.Visible;
            await RefreshAsync(); UpdateControls();
        });
    }
    private async void OnTrim(object Sender, RoutedEventArgs Args)
    {
        if (Selected is not { } Item) return;
        var Start = new NumberBox { Header = "Start (seconds)", Minimum = 0, Maximum = Item.Duration.TotalSeconds, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var End = new NumberBox { Header = "End (seconds)", Minimum = 0, Maximum = Item.Duration.TotalSeconds, Value = Item.Duration.TotalSeconds, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var Panel = new StackPanel { Spacing = 16, MinWidth = 340 }; Panel.Children.Add(new TextBlock { Text = "Save a trimmed copy. Your original stays intact." }); Panel.Children.Add(Start); Panel.Children.Add(End);
        if (await ShowDialogAsync(new ContentDialog { Title = "Trim recording", Content = Panel, PrimaryButtonText = "Save copy", CloseButtonText = "Cancel" }) == ContentDialogResult.Primary)
            await RunAsync(async () => { if (!double.IsFinite(Start.Value) || !double.IsFinite(End.Value)) throw new ArgumentException("Enter valid times."); var Output = await Task.Run(() => LibraryService.Trim(Item, TimeSpan.FromSeconds(Start.Value), TimeSpan.FromSeconds(End.Value))); await RefreshAsync(Output); });
    }
    private async void OnExportMp3(object Sender, RoutedEventArgs Args) => await ExportAsync(".mp3");
    private async void OnExportFlac(object Sender, RoutedEventArgs Args) => await ExportAsync(".flac");
    private async Task ExportAsync(string Extension)
    {
        if (Selected is not { } Item) return;
        await RunAsync(async () =>
        {
            var Picker = new FileSavePicker { SuggestedFileName = Item.Name };
            Picker.FileTypeChoices.Add(Extension == ".mp3" ? "MP3 audio" : "FLAC lossless audio", new[] { Extension });
            WinRT.Interop.InitializeWithWindow.Initialize(Picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var File = await Picker.PickSaveFileAsync();
            if (File == null) return;
            Busy = true; UpdateControls(); StateText.Text = "Exporting…";
            try { await Task.Run(() => LibraryService.Export(Item.Path, File.Path, Settings.Mp3Bitrate)); ShowNotice("Export complete", File.Path, InfoBarSeverity.Success); }
            finally { Busy = false; UpdateControls(); }
        });
    }
    private void OnCopyPath(object Sender, RoutedEventArgs Args)
    {
        if (Selected == null) return;
        var Data = new DataPackage(); Data.SetText(Selected.Path); Clipboard.SetContent(Data);
        ShowNotice("Path copied", "The recording’s file path is on your clipboard.", InfoBarSeverity.Success);
    }
    private void OnOpenLocation(object Sender, RoutedEventArgs Args)
    {
        if (Selected != null) { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Selected.Path}\"") { UseShellExecute = true }); } catch (Exception Error) { ShowError(Error); } }
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog Dialog)
    {
        if (DialogOpen) return ContentDialogResult.None;
        DialogOpen = true; Dialog.XamlRoot = Root.XamlRoot; Dialog.RequestedTheme = Root.RequestedTheme;
        try { return await Dialog.ShowAsync(); } finally { DialogOpen = false; }
    }
    private async Task RunAsync(Func<Task> Action)
    {
        try { await Action(); } catch (Exception Error) { ShowError(Error); }
    }
    private void ShowError(Exception Error) { AppPaths.Log(Error); ShowNotice("Something needs attention", Error.Message, InfoBarSeverity.Error); }
    private void ShowNotice(string Title, string Message, InfoBarSeverity Severity) { Notice.Title = Title; Notice.Message = Message; Notice.Severity = Severity; Notice.IsOpen = true; }
    private static string FormatTime(TimeSpan Time) => Time.TotalHours >= 1 ? Time.ToString(@"h\:mm\:ss") : $"{(int)Time.TotalMinutes:00}:{Time.Seconds:00}";

    private async Task RecoverAsync()
    {
        if (!Directory.Exists(AppPaths.Sessions)) return;
        var Failed = 0; var Recovered = 0;
        foreach (var Metadata in Directory.GetFiles(AppPaths.Sessions, "*.json"))
        {
            if (!File.Exists(Path.ChangeExtension(Metadata, ".pcm"))) continue;
            try { await Task.Run(() => SessionJournal.Recover(Metadata)); Recovered++; }
            catch (Exception Error) { AppPaths.Log(Error); Failed++; }
        }
        if (Recovered > 0) { await RefreshAsync(); ShowNotice("Audio recovered", $"Restored {Recovered} interrupted recording(s).", InfoBarSeverity.Success); }
        if (Failed > 0) ShowNotice("Recovery needs a folder", $"{Failed} session(s) remain safely stored. Use Recover recordings in Settings to save them to your current folder.", InfoBarSeverity.Warning);
    }
    private async void OnClosing(AppWindow Sender, AppWindowClosingEventArgs Args)
    {
        if (Closing) return;
        Args.Cancel = true;
        if (Busy || DialogOpen) { ShowNotice("Please wait", "Finish the current operation before closing Wavely.", InfoBarSeverity.Informational); return; }
        await StopRecordingAsync(false);
        await Playback.DisposeAsync();
        Timer.Stop(); Shell.Dispose(); Closing = true; Close();
    }
}
