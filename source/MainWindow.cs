using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ViperPc;

public sealed class MainWindow : Window
{
    readonly FeatureCatalog catalog = new();
    readonly AppPreferences preferences;
    SettingsState State => preferences.Routes[preferences.Route];
    readonly DspEngine engine = new();
    readonly AudioHost audio = new();
    readonly SystemAudioHost systemAudio = new();
    readonly VirtualAudioRoute systemRoute = new(AppPreferences.DataPath);
    ScrollViewer? contentScroll;
    string renderedPage = "";
    readonly Dictionary<string, double> scrollOffsets = new();
    TextBlock? systemStatusText;
    ApoBridge? apo;
    DspEngine? apoRateEngine;
    readonly Grid root = new();
    StackPanel body = new();
    TextBlock status = new();
    TextBlock meters = new();
    TextBlock? apoStatusText;
    readonly HashSet<string> expanded = new() { "36868", "65551" };
    readonly Dictionary<string, FrameworkElement> controlElements = new();
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly System.Windows.Forms.NotifyIcon tray;
    string page = "Эффекты";
    string search = "";
    string lastNotice = "Обработка выключена";
    bool quitting, building;
    string endpointId = "";
    int pollCount;
    long apoLastFrames, apoProgressTick;
    bool systemRouteRestoreFailed;
    public MainWindow(bool driverPage = false, bool previewOnly = false)
    {
        if (driverPage) page = "Драйвер";
        preferences = AppPreferences.Load(catalog);
        if (!previewOnly) { try { systemRoute.Restore(); } catch (Exception e) { lastNotice = e.Message; } }
        if (preferences.AutoSwitchProfiles) SyncEndpointProfile();
        Title = "ViPER4Windows-RE · 0.6.2";
        Width = 1140; Height = 860; MinWidth = 900; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        ApplyTheme();
        Content = root;
        tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "ViPER4Windows-RE", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        menu.Items.Add("Включить / выключить", null, (_, _) => Dispatcher.Invoke(() => { State.Set("36868", !State.Bool("36868")); Changed(); Render(); }));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(Quit)); tray.ContextMenuStrip = menu;
        audio.Status += text => Dispatcher.InvokeAsync(() => Notice(text));
        systemAudio.Status += text => Dispatcher.InvokeAsync(() => Notice(text));
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); preferences.Save(); };
        poll.Tick += (_, _) => Poll(); if (!previewOnly) poll.Start();
        SourceInitialized += (_, _) => { int v = preferences.LightTheme ? 0 : 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref v, 4); };
        Closing += (_, e) => { if (!quitting && preferences.CloseToTray) { e.Cancel = true; Hide(); } else { poll.Stop(); saveTimer.Stop(); if (!previewOnly) preferences.Save(); audio.Stop(); if (!previewOnly) StopSystemAudio(); systemAudio.Dispose(); apo?.Disable(); apo?.Dispose(); apoRateEngine?.Dispose(); engine.Dispose(); tray.Dispose(); } };
        try { ParameterMapping.Apply(engine, State); } catch (Exception e) { Notice(e.Message); }
        Render();
        if (!previewOnly && preferences.StartSystemAudio) Loaded += (_, _) => { try { StartSystemAudio(); } catch (Exception e) { Notice(e.Message); } };
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    Brush B(string key) => (Brush)Resources[key];
    void ApplyTheme()
    {
        bool light = preferences.LightTheme;
        var colors = light
            ? new Dictionary<string, string> { ["Bg"] = "#F7F5FB", ["Side"] = "#ECE8F4", ["Surface"] = "#FFFFFF", ["Soft"] = "#F0EBF9", ["Text"] = "#292334", ["Muted"] = "#756D80", ["Accent"] = "#6200EE", ["Line"] = "#E3DCEA" }
            : new Dictionary<string, string> { ["Bg"] = "#141816", ["Side"] = "#191F1B", ["Surface"] = "#202722", ["Soft"] = "#2A342D", ["Text"] = "#E8EFEA", ["Muted"] = "#99A79C", ["Accent"] = "#00E676", ["Line"] = "#343E37" };
        foreach (var c in colors) Resources[c.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.Value));
        Background = B("Bg"); Foreground = B("Text");
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, B("Text")));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, B("Soft")));
        buttonStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(15, 10, 15, 10)));
        buttonStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='9' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter Property='Opacity' Value='0.8'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate>")));
        Resources[typeof(Button)] = buttonStyle;
        var boxStyle = new Style(typeof(TextBox));
        boxStyle.Setters.Add(new Setter(Control.BackgroundProperty, B("Soft"))); boxStyle.Setters.Add(new Setter(Control.ForegroundProperty, B("Text")));
        boxStyle.Setters.Add(new Setter(Control.BorderBrushProperty, B("Line"))); boxStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        Resources[typeof(TextBox)] = boxStyle;
        var checkStyle = new Style(typeof(CheckBox)); checkStyle.Setters.Add(new Setter(Control.ForegroundProperty, B("Text"))); Resources[typeof(CheckBox)] = checkStyle;
        var sliderStyle = new Style(typeof(Slider)); sliderStyle.Setters.Add(new Setter(Control.ForegroundProperty, B("Accent"))); Resources[typeof(Slider)] = sliderStyle;
        var controls = (ResourceDictionary)XamlReader.Parse("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <ControlTemplate x:Key="SliderH" TargetType="Slider">
  <Grid Height="28">
   <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}">
    <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Background="{DynamicResource Accent}" CornerRadius="2"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
    <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Background="{DynamicResource Line}" CornerRadius="2"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
    <Track.Thumb><Thumb Width="15" Height="15"><Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse Fill="{DynamicResource Accent}"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
   </Track>
  </Grid>
 </ControlTemplate>
 <ControlTemplate x:Key="SliderV" TargetType="Slider">
  <Grid Width="28">
   <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="False" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}">
    <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Width="4" Background="{DynamicResource Accent}" CornerRadius="2"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
    <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Width="4" Background="{DynamicResource Line}" CornerRadius="2"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
    <Track.Thumb><Thumb Width="15" Height="15"><Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse Fill="{DynamicResource Accent}"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
   </Track>
  </Grid>
 </ControlTemplate>
 <Style TargetType="Slider"><Setter Property="Template" Value="{StaticResource SliderH}"/><Style.Triggers><Trigger Property="Orientation" Value="Vertical"><Setter Property="Template" Value="{StaticResource SliderV}"/></Trigger></Style.Triggers></Style>
 <Style TargetType="ComboBox">
  <Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Template"><Setter.Value>
   <ControlTemplate TargetType="ComboBox"><Grid>
    <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="{DynamicResource Soft}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="8"><TextBlock Text="▾" Foreground="{DynamicResource Muted}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,11,0"/></Border></ControlTemplate></ToggleButton.Template></ToggleButton>
    <ContentPresenter Margin="11,9,30,9" IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}" VerticalAlignment="Center"/>
    <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" IsOpen="{TemplateBinding IsDropDownOpen}" Focusable="False"><Border Background="{DynamicResource Surface}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="8" MinWidth="{TemplateBinding ActualWidth}"><ScrollViewer MaxHeight="280"><StackPanel IsItemsHost="True"/></ScrollViewer></Border></Popup>
   </Grid></ControlTemplate>
  </Setter.Value></Setter>
 </Style>
 <Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="{DynamicResource Text}"/><Setter Property="Padding" Value="12,9"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="highlight" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="highlight" Property="Background" Value="{DynamicResource Soft}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType="ScrollBar"><Setter Property="Width" Value="9"/><Setter Property="Background" Value="Transparent"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar"><Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True"><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="{DynamicResource Line}" CornerRadius="5" Margin="2,0"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>
""");
        foreach (var key in controls.Keys) Resources[key] = controls[key];
    }
    TextBlock Text(string text, double size = 14, string color = "Text", bool bold = false)
        => new() { Text = text, FontSize = size, Foreground = B(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    Button Button(string text, Action action, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 10, 0) };
        if (primary) { b.Background = B("Accent"); b.Foreground = preferences.LightTheme ? Brushes.White : Brushes.Black; }
        b.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(e.Message, "ViPER4Windows-RE", MessageBoxButton.OK, MessageBoxImage.Information); Notice(e.Message); } };
        return b;
    }
    CheckBox Switch(bool value, Action<bool> changed)
    {
        var check = new CheckBox { IsChecked = value, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Focusable = true };
        string accent = preferences.LightTheme ? "#6200EE" : "#00E676";
        check.Template = (ControlTemplate)XamlReader.Parse($"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='CheckBox'><Border x:Name='track' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Width='44' Height='25' Background='#526258' CornerRadius='13'><Ellipse x:Name='knob' Width='19' Height='19' Fill='#E8EFEA' HorizontalAlignment='Left' Margin='3,0,0,0'/></Border><ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='track' Property='Background' Value='{accent}'/><Setter TargetName='knob' Property='HorizontalAlignment' Value='Right'/><Setter TargetName='knob' Property='Margin' Value='0,0,3,0'/><Setter TargetName='knob' Property='Fill' Value='#18231C'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
        check.Checked += (_, _) => { if (!building) changed(true); }; check.Unchecked += (_, _) => { if (!building) changed(false); };
        return check;
    }
    void Render()
    {
        if (contentScroll != null) scrollOffsets[renderedPage] = contentScroll.VerticalOffset;
        double offset = scrollOffsets.GetValueOrDefault(page);
        renderedPage = page;
        systemStatusText = null;
        building = true; root.Children.Clear(); root.ColumnDefinitions.Clear(); root.RowDefinitions.Clear(); body = new(); status = new(); meters = new(); controlElements.Clear();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(222) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Border { Background = B("Side"), Padding = new Thickness(24, 30, 20, 22) };
        var side = new DockPanel(); sidebar.Child = side;
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(Text("ViPER4Android-RE", 12, "Muted")); bottom.Children.Add(Text("0.6.2 → Windows x64", 11, "Muted"));
        var about = Button("О переносе", () => { page = "Драйвер"; Render(); }); about.Margin = new Thickness(0, 16, 0, 0); bottom.Children.Add(about); side.Children.Add(bottom);
        var nav = new StackPanel();
        nav.Children.Add(Text("ViPER", 35, "Accent", true)); nav.Children.Add(Text("4 Windows · RE", 14, "Muted"));
        nav.Children.Add(new Border { Height = 30 });
        foreach (var name in new[] { "Эффекты", "Пресеты", "Звук", "Драйвер", "Настройки" })
        {
            var label = name == "Эффекты" ? "◉  Эффекты" : name == "Пресеты" ? "▤  Пресеты" : name == "Звук" ? "♫  Звук" : name == "Драйвер" ? "◎  Драйвер" : "⚙  Настройки";
            var b = Button(label, () => { page = name; Render(); }); b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 4, 0, 4);
            if (page == name) { b.Background = B("Soft"); b.Foreground = B("Accent"); } else b.Background = Brushes.Transparent;
            nav.Children.Add(b);
        }
        side.Children.Add(nav); root.Children.Add(sidebar);
        var main = new DockPanel { Margin = new Thickness(30, 26, 30, 16) }; Grid.SetColumn(main, 1); root.Children.Add(main);
        var top = new Grid { Margin = new Thickness(0, 0, 0, 22) }; top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel(); heading.Children.Add(Text(page, 30, "Text", true));
        heading.Children.Add(Text(page == "Эффекты" ? "Исходные настройки ViPER4Android · 18 разделов" : page == "Пресеты" ? "Пресеты Android XML и настройки каждого выхода" : page == "Звук" ? "Обработка файла, входного устройства или звука Windows" : page == "Драйвер" ? "Состояние обработки и подключение системного звука" : "Внешний вид и поведение приложения", 13, "Muted"));
        top.Children.Add(heading);
        var power = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        power.Children.Add(Text("Обработка  ", 13, "Muted")); power.Children.Add(Switch(State.Bool("36868"), v => { State.Set("36868", v); Changed(); Render(); })); Grid.SetColumn(power, 1); top.Children.Add(power);
        DockPanel.SetDock(top, Dock.Top); main.Children.Add(top);
        var foot = new Border { Padding = new Thickness(4, 12, 4, 0), BorderBrush = B("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 16, 0, 0) };
        var footPanel = new DockPanel(); meters.Foreground = B("Muted"); meters.FontSize = 11; meters.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(meters, Dock.Right); footPanel.Children.Add(meters);
        status.Foreground = B("Muted"); status.FontSize = 12; status.Text = lastNotice; status.TextTrimming = TextTrimming.CharacterEllipsis; footPanel.Children.Add(status); foot.Child = footPanel; DockPanel.SetDock(foot, Dock.Bottom); main.Children.Add(foot);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 12, 0), Content = body };
        main.Children.Add(scroll);
        contentScroll = scroll;
        if (page == "Эффекты") RenderEffects(); else if (page == "Пресеты") RenderPresets(); else if (page == "Звук") RenderAudio(); else if (page == "Драйвер") RenderDriver(); else RenderSettings();
        building = false;
        UpdateLayout(); scroll.ScrollToVerticalOffset(offset); UpdateLayout();
    }
    Border Panel(FrameworkElement content, double padding = 20)
        => new() { Background = B("Surface"), BorderBrush = B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(padding), Child = content, Margin = new Thickness(0, 0, 0, 14) };
    ComboBox Combo(IEnumerable<string> choices, string value, Action<string> action)
    {
        var combo = new ComboBox { ItemsSource = choices.ToArray(), SelectedItem = value, MinWidth = 165, MaxWidth = 350, Padding = new Thickness(9, 7, 9, 7), Background = B("Soft"), Foreground = B("Text"), VerticalAlignment = VerticalAlignment.Center };
        combo.SelectionChanged += (_, _) => { if (!building && combo.SelectedItem is string selected) action(selected); };
        return combo;
    }
    void RenderEffects()
    {
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 20) }; toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filter = new TextBox { Text = search, ToolTip = "Поиск эффекта", Margin = new Thickness(0, 0, 15, 0), VerticalContentAlignment = VerticalAlignment.Center };
        filter.TextChanged += (_, _) => { if (building) return; search = filter.Text; foreach (var child in body.Children.OfType<Border>()) if (child.Tag is EffectDef fx) child.Visibility = Matches(fx) ? Visibility.Visible : Visibility.Collapsed; };
        var searchPanel = new Grid(); searchPanel.Children.Add(filter); var placeholder = Text("Поиск эффекта…", 13, "Muted"); placeholder.IsHitTestVisible = false; placeholder.Margin = new Thickness(11, 0, 20, 0); placeholder.Visibility = search.Length == 0 ? Visibility.Visible : Visibility.Collapsed; searchPanel.Children.Add(placeholder); filter.TextChanged += (_, _) => placeholder.Visibility = filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; toolbar.Children.Add(searchPanel);
        var route = Combo(preferences.Routes.Keys, preferences.Route, value => { audio.Stop(); preferences.Route = value; Changed(); Render(); }); Grid.SetColumn(route, 1); toolbar.Children.Add(route); body.Children.Add(toolbar);
        foreach (var fx in catalog.Effects)
        {
            var container = new StackPanel();
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(EffectIcon(fx));
            var title = new StackPanel(); title.Children.Add(Text(fx.Title, 17, "Text", true));
            string subtitle = fx.Key == "36868" ? "Громкость, баланс каналов и ограничение пиков" : State.Bool(fx.Key) ? "Включено" : "Выключено";
            var subtitleLabel = Text(subtitle, 12, "Muted"); title.Children.Add(subtitleLabel); title.Margin = new Thickness(6, 0, 10, 0);
            title.Cursor = Cursors.Hand;
            Grid.SetColumn(title, 1); header.Children.Add(title);
            var enabled = Switch(State.Bool(fx.Key), v => { State.Set(fx.Key, v); subtitleLabel.Text = v ? "Включено" : "Выключено"; Changed(); if (fx.Key == "36868") Render(); }); Grid.SetColumn(enabled, 2); header.Children.Add(enabled); container.Children.Add(header);
            var content = new StackPanel { Margin = new Thickness(44, 22, 0, 0), Visibility = expanded.Contains(fx.Key) ? Visibility.Visible : Visibility.Collapsed };
            title.MouseLeftButtonUp += (_, e) => { if (!expanded.Add(fx.Key)) expanded.Remove(fx.Key); content.Visibility = expanded.Contains(fx.Key) ? Visibility.Visible : Visibility.Collapsed; e.Handled = true; };
            foreach (var c in fx.Controls) { var ui = BuildControl(c); controlElements[c.Key] = ui; content.Children.Add(ui); }
            if (fx.Controls.Count == 0) content.Children.Add(Text(fx.Key == "65583" ? "Модель лампового усилителя 6N1J." : "Эффект включается переключателем.", 13, "Muted"));
            var reset = Button("Сбросить этот раздел", () => { State.Values[fx.Key] = fx.DefaultValue.Clone(); foreach (var c in fx.Controls) State.Values[c.Key] = c.DefaultValue.Clone(); Changed(); Render(); }); reset.HorizontalAlignment = HorizontalAlignment.Left; reset.Margin = new Thickness(0, 10, 0, 0); content.Children.Add(reset);
            container.Children.Add(content); var panel = Panel(container); panel.Tag = fx; panel.Visibility = Matches(fx) ? Visibility.Visible : Visibility.Collapsed; body.Children.Add(panel);
        }
        UpdateDependencies();
    }
    bool Matches(EffectDef fx) => search.Length == 0 || fx.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || fx.Controls.Any(x => x.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
    FrameworkElement EffectIcon(EffectDef fx)
    {
        var name = fx.KeyResource.Replace("string/key_", "");
        var names = new Dictionary<string, string> { ["36868"]="ic_power",["65565"]="ic_playback_control",["65600"]="ic_compressor",["65540"]="ic_viperddc",["65548"]="ic_vse",["65551"]="ic_equalizer",["65538"]="ic_convolver",["65553"]="ic_surround",["65557"]="ic_diff_surround",["65559"]="ic_headphone_surround",["65560"]="ic_reverb",["65569"]="ic_dynamic_system",["65583"]="ic_tubeamp",["65574"]="ic_bass",["65578"]="ic_clarity",["65582"]="ic_protection",["65589"]="ic_analogx",["65593"]="ic_speaker" };
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "icons", (fx.Icon.Length > 0 ? fx.Icon : names.GetValueOrDefault(fx.Key) ?? "ic_equalizer") + ".svg");
            var doc = XDocument.Load(path); var canvas = new Canvas { Width = 24, Height = 24 };
            foreach (var p in doc.Descendants().Where(x => x.Name.LocalName == "path"))
            {
                bool filled = (string?)p.Attribute("fill") != "none"; string stroke = (string?)p.Attribute("stroke") ?? "none";
                if (!filled && stroke == "none") continue;
                double sw = double.TryParse((string?)p.Attribute("stroke-width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ? width : 1;
                canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse((string?)p.Attribute("d") ?? ""), Fill = filled ? B("Accent") : null, Stroke = stroke != "none" ? B("Accent") : null, StrokeThickness = sw, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            }
            return new Viewbox { Child = canvas, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        }
        catch { return Text("◉", 22, "Accent"); }
    }
    FrameworkElement BuildControl(ControlDef c)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 19) };
        if (c.Kind.Contains("GraphicEqualizer")) { p.Children.Add(Equalizer(c)); return p; }
        var label = new Grid(); label.ColumnDefinitions.Add(new ColumnDefinition()); label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); label.Children.Add(Text(c.Title, 14)); p.Children.Add(label);
        if (c.Kind.Contains("SeekBar"))
        {
            var value = new TextBox { Text = State.Int(c.Key).ToString(), Width = 50, FontSize = 12, Padding = new Thickness(4, 2, 4, 2), TextAlignment = TextAlignment.Center, ToolTip = "Индекс исходного регулятора. Значение справа пересчитывается как в Android." };
            var display = Text(ParameterMapping.Display(c, State), 12, "Accent"); display.Margin = new Thickness(10, 0, 0, 0);
            var end = new StackPanel { Orientation = Orientation.Horizontal }; end.Children.Add(value); end.Children.Add(display); Grid.SetColumn(end, 1); label.Children.Add(end);
            var slider = new Slider { Minimum = c.Min, Maximum = c.Max, TickFrequency = 1, IsSnapToTickEnabled = true, Value = State.Int(c.Key), Margin = new Thickness(0, 9, 0, 0) };
            slider.ValueChanged += (_, _) => { if (building) return; State.Set(c.Key, (int)Math.Round(slider.Value)); value.Text = State.Int(c.Key).ToString(); display.Text = ParameterMapping.Display(c, State); Changed(); };
            void Commit() { if (int.TryParse(value.Text, out int n)) slider.Value = Math.Clamp(n, c.Min, c.Max); else value.Text = State.Int(c.Key).ToString(); }
            value.LostFocus += (_, _) => Commit(); value.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            p.Children.Add(slider);
        }
        else if (c.Kind.Contains("CheckBox"))
        {
            var sw = Switch(State.Bool(c.Key), v => { State.Set(c.Key, v); Changed(); UpdateDependencies(); }); Grid.SetColumn(sw, 1); label.Children.Add(sw);
        }
        else if (c.Key == "65547" || c.Key == "65540;65541;65542")
        {
            var asset = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var select = Button("Выбрать…", () => ImportAsset(c)); DockPanel.SetDock(select, Dock.Right); asset.Children.Add(select);
            var clear = Button("×", () => { State.Set(c.Key, ""); Changed(); Render(); }); clear.ToolTip = "Удалить выбранный файл"; DockPanel.SetDock(clear, Dock.Right); asset.Children.Add(clear);
            asset.Children.Add(Text(string.IsNullOrEmpty(State.Text(c.Key)) ? "Файл не выбран" : System.IO.Path.GetFileName(State.Text(c.Key)), 12, "Muted")); p.Children.Add(asset);
        }
        else
        {
            var entries = c.Entries; var values = c.EntryValues;
            int idx = Array.IndexOf(values, State.Text(c.Key));
            var combo = Combo(entries, idx >= 0 && idx < entries.Length ? entries[idx] : entries.FirstOrDefault() ?? "", val => { int i = Array.IndexOf(entries, val); if (i >= 0 && i < values.Length) State.Set(c.Key, values[i]); Changed(); }); combo.HorizontalAlignment = HorizontalAlignment.Stretch; combo.Margin = new Thickness(0, 9, 0, 0); p.Children.Add(combo);
        }
        return p;
    }
    void UpdateDependencies()
    {
        foreach (var fx in catalog.Effects) foreach (var c in fx.Controls)
            if (!string.IsNullOrEmpty(c.Dependency) && controlElements.TryGetValue(c.Key, out var ui))
            {
                var dependency = catalog.Effects.SelectMany(x => x.Controls).FirstOrDefault(x => x.Key == c.Dependency);
                ui.IsEnabled = dependency?.DisableDependentsState == true ? !State.Bool(c.Dependency) : State.Bool(c.Dependency);
            }
    }
    FrameworkElement Equalizer(ControlDef c)
    {
        float[] gains = ParseGains(State.Text(c.Key));
        var p = new StackPanel();
        var plot = new EqPlot(gains, B("Accent"), B("Line"), B("Muted")) { Height = 116, Margin = new Thickness(0, 0, 0, 15) }; p.Children.Add(plot);
        var sliders = new Grid(); var readouts = new TextBlock[10];
        string[] labels = { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
        for (int i = 0; i < 10; i++)
        {
            int k = i; sliders.ColumnDefinitions.Add(new ColumnDefinition()); var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var readout = Text(gains[i].ToString("0.0", CultureInfo.InvariantCulture), 11, "Accent"); readout.HorizontalAlignment = HorizontalAlignment.Center; readouts[i] = readout; column.Children.Add(readout);
            var slider = new Slider { Minimum = -12, Maximum = 12, TickFrequency = 0.25, IsSnapToTickEnabled = true, Orientation = Orientation.Vertical, Height = 126, Value = gains[i], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8), ToolTip = "Двойной щелчок: 0 дБ" };
            slider.ValueChanged += (_, _) => { if (building) return; gains[k] = (float)slider.Value; readouts[k].Text = gains[k].ToString("0.0", CultureInfo.InvariantCulture); State.Set(c.Key, string.Join(";", gains.Select(x => x.ToString(CultureInfo.InvariantCulture))) + ";"); plot.InvalidateVisual(); Changed(); };
            slider.MouseDoubleClick += (_, _) => slider.Value = 0;
            column.Children.Add(slider); var hz = Text(labels[i], 11, "Muted"); hz.HorizontalAlignment = HorizontalAlignment.Center; column.Children.Add(hz); Grid.SetColumn(column, i); sliders.Children.Add(column);
        }
        p.Children.Add(sliders);
        var row = new DockPanel { Margin = new Thickness(0, 16, 0, 0) }; var neutral = Button("0 дБ", () => { State.Set(c.Key, "0;0;0;0;0;0;0;0;0;0;"); Changed(); Render(); }); DockPanel.SetDock(neutral, Dock.Right); row.Children.Add(neutral);
        var names = catalog.Array("equalizer_preset_modes"); var values = catalog.Array("equalizer_preset_values");
        var presets = Combo(names.Prepend("Исходный пресет EQ…"), "Исходный пресет EQ…", selected => { int idx = Array.IndexOf(names, selected); if (idx >= 0) { State.Set(c.Key, values[idx]); Changed(); Render(); } }); row.Children.Add(presets); p.Children.Add(row);
        p.Children.Add(Text("±12 дБ · пресет Flat сохранён как в APK: +1,5 дБ", 11, "Muted"));
        return p;
    }
    public static float[] ParseGains(string text)
    {
        var values = text.Split(';', StringSplitOptions.RemoveEmptyEntries); var result = new float[10];
        for (int i = 0; i < Math.Min(10, values.Length); i++) if (float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)) result[i] = Math.Clamp(v, -12, 12);
        return result;
    }
    void ImportAsset(ControlDef c)
    {
        bool isDdc = c.Key == "65547";
        var dialog = new OpenFileDialog { Filter = isDdc ? "DDC (*.vdc)|*.vdc|Все файлы|*.*" : "Импульсы (*.irs;*.wav)|*.irs;*.wav|Все файлы|*.*" };
        if (dialog.ShowDialog() != true) return;
        string folder = System.IO.Path.Combine(AppPreferences.DataPath, isDdc ? "ddc" : "kernels"); Directory.CreateDirectory(folder);
        string basename = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        string target = System.IO.Path.Combine(folder, basename + "-" + Guid.NewGuid().ToString("N")[..8] + System.IO.Path.GetExtension(dialog.FileName));
        if (!System.IO.Path.GetFullPath(dialog.FileName).Equals(System.IO.Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(dialog.FileName, target, true);
        if (isDdc) engine.LoadDdc(target); else engine.LoadIr(target);
        State.Set(c.Key, target); Changed(); Render();
    }
    void RenderPresets()
    {
        var p = new StackPanel(); p.Children.Add(Text("Пресет для «" + preferences.Route + "»", 19, "Text", true));
        p.Children.Add(Text("Настройки наушников, динамиков и Bluetooth сохраняются отдельно. XML можно перенести обратно в ViPER4Android-RE.", 13, "Muted"));
        var name = new TextBox { Text = "Мой пресет", Margin = new Thickness(0, 16, 0, 16) }; p.Children.Add(name);
        var actions = new WrapPanel(); actions.Children.Add(Button("Сохранить", () => { string clean = string.Concat(name.Text.Where(x => !System.IO.Path.GetInvalidFileNameChars().Contains(x))).Trim(); if (clean.Length == 0) throw new InvalidDataException("Введите имя."); Directory.CreateDirectory(PresetFolder); string path = System.IO.Path.Combine(PresetFolder, clean + ".xml"); if (File.Exists(path) && MessageBox.Show("Перезаписать пресет «" + clean + "»?", "Пресеты", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; State.Export(path); Notice("Пресет сохранён"); Render(); }, true));
        actions.Children.Add(Button("Импорт XML / JSON", () => { var d = new OpenFileDialog { Filter = "Пресеты (*.xml;*.json)|*.xml;*.json|Все файлы|*.*" }; if (d.ShowDialog() == true) { State.Import(d.FileName, catalog); Changed(); Render(); } }));
        actions.Children.Add(Button("Экспорт…", () => { var d = new SaveFileDialog { FileName = "ViPER-preset.xml", Filter = "Android XML|*.xml|JSON|*.json" }; if (d.ShowDialog() == true) State.Export(d.FileName); })); p.Children.Add(actions); body.Children.Add(Panel(p));
        Directory.CreateDirectory(PresetFolder);
        var presets = Directory.GetFiles(PresetFolder, "*.xml");
        if (presets.Length == 0) body.Children.Add(Text("Сохранённые пресеты появятся здесь.", 14, "Muted"));
        foreach (var path in presets)
        {
            var row = new DockPanel(); var load = Button("Загрузить", () => { State.Import(path, catalog); Changed(); Notice("Загружен «" + System.IO.Path.GetFileNameWithoutExtension(path) + "»"); page = "Эффекты"; Render(); }); DockPanel.SetDock(load, Dock.Right); row.Children.Add(load);
            var remove = Button("Удалить", () => { if (MessageBox.Show("Удалить пресет «" + System.IO.Path.GetFileNameWithoutExtension(path) + "»?", "Пресеты", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { File.Delete(path); Render(); } }); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var rename = new TextBox { Text = System.IO.Path.GetFileNameWithoutExtension(path), Margin = new Thickness(0, 0, 10, 0), ToolTip = "Введите новое имя, затем нажмите «Имя»" };
            var renameButton = Button("Имя", () => { string clean = string.Concat(rename.Text.Where(x => !System.IO.Path.GetInvalidFileNameChars().Contains(x))).Trim(); if (clean.Length == 0) throw new InvalidDataException("Введите имя."); string next = System.IO.Path.Combine(PresetFolder, clean + ".xml"); if (!next.Equals(path, StringComparison.OrdinalIgnoreCase)) File.Move(path, next); Render(); }); DockPanel.SetDock(renameButton, Dock.Right); row.Children.Add(renameButton); row.Children.Add(rename); body.Children.Add(Panel(row));
        }
    }
    string PresetFolder => System.IO.Path.Combine(AppPreferences.DataPath, "presets");
    string SystemStatus() => systemAudio.IsRunning
        ? $"Поток работает · 48 кГц / float / стерео · {systemAudio.ProcessedFrames:N0} кадров\nВывод: {(systemAudio.RawOutputEnabled ? "прямой RAW" : "обычный Windows; эффекты выходного устройства могут сохраняться")}\nОчередь {systemAudio.QueueMilliseconds:0} мс · выход {systemAudio.DeviceLatencyMilliseconds:0} мс · разрывы {systemAudio.Discontinuities} · пропуски {systemAudio.DroppedFrames} · нехватки {systemAudio.Underruns}"
        : "Поток остановлен" + (systemAudio.Error.Length > 0 ? " · " + systemAudio.Error : "");
    void RenderSystemAudio()
    {
        var p = new StackPanel(); p.Children.Add(Text("Виртуальное устройство ViPER", 20, "Text", true));
        p.Children.Add(Text("Все приложения → ViPER → выбранные наушники или колонки. При запуске ViPER становится выходом Windows; при остановке возвращается прежний выход. Приложения с собственным выбором устройства направьте на ViPER.", 13, "Muted"));
        var endpoints = WindowsAudio.Outputs();
        var virtuals = endpoints.Where(x => x.IsVirtual).ToArray(); var physical = endpoints.Where(x => !x.IsVirtual).ToArray();
        if (!virtuals.Any(x => x.Id == preferences.VirtualEndpointId)) preferences.VirtualEndpointId = virtuals.FirstOrDefault(x => x.IsFxSound)?.Id ?? virtuals.FirstOrDefault()?.Id ?? "";
        if (!physical.Any(x => x.Id == preferences.PhysicalOutputId)) preferences.PhysicalOutputId = physical.FirstOrDefault(x => x.Id == CoreAudioProbe.DefaultOutput()?.Id)?.Id ?? physical.FirstOrDefault()?.Id ?? "";
        p.Children.Add(Text("Устройство Windows для звука приложений", 13, "Muted"));
        var source = new ComboBox { ItemsSource = virtuals, SelectedItem = virtuals.FirstOrDefault(x => x.Id == preferences.VirtualEndpointId), DisplayMemberPath = "Name", Margin = new Thickness(0, 8, 0, 14), IsEnabled = !systemAudio.IsRunning };
        source.SelectionChanged += (_, _) => { if (!building && source.SelectedItem is RenderEndpoint selected) { preferences.VirtualEndpointId = selected.Id; preferences.Save(); } }; p.Children.Add(source);
        p.Children.Add(Text("Куда выводить обработанный звук", 13, "Muted"));
        var output = new ComboBox { ItemsSource = physical, SelectedItem = physical.FirstOrDefault(x => x.Id == preferences.PhysicalOutputId), DisplayMemberPath = "Name", Margin = new Thickness(0, 8, 0, 14), IsEnabled = !systemAudio.IsRunning };
        output.SelectionChanged += (_, _) => { if (!building && output.SelectedItem is RenderEndpoint selected) { preferences.PhysicalOutputId = selected.Id; preferences.Save(); } }; p.Children.Add(output);
        var buttons = new WrapPanel();
        buttons.Children.Add(Button(systemAudio.IsRunning ? "Остановить системный звук" : "Запустить системный звук", () => { if (systemAudio.IsRunning) StopSystemAudio(); else StartSystemAudio(); Render(); }, true));
        buttons.Children.Add(Button("Назвать устройство ViPER", PrepareVirtualDevice));
        buttons.Children.Add(Button("Обновить устройства", Render));
        buttons.Children.Add(Button("Вернуть имя устройства", () => { StopSystemAudio(); systemRoute.RestoreName(); Render(); })); p.Children.Add(buttons);
        var automatic = new CheckBox { Content = "Запускать этот звуковой маршрут при открытии программы", IsChecked = preferences.StartSystemAudio, Margin = new Thickness(0, 14, 0, 8) };
        automatic.Click += (_, _) => { preferences.StartSystemAudio = automatic.IsChecked == true; preferences.Save(); }; p.Children.Add(automatic);
        systemStatusText = Text(SystemStatus(), 12, "Muted"); p.Children.Add(systemStatusText);
        p.Children.Add(Text(virtuals.Length == 0 ? "Виртуальный драйвер не найден. Установите подписанный виртуальный аудиодрайвер, затем обновите список." : "На этом ПК устройство ViPER использует установленный виртуальный драйвер FxSound. Приложение FxSound должно быть закрыто. APO для этого маршрута не требуется.", 12, "Muted"));
        body.Children.Add(Panel(p));
    }
    void StartSystemAudio()
    {
        if (systemAudio.IsRunning) return;
        audio.Stop(); apo?.Disable(); apo?.Dispose(); apo = null;
        var output = WindowsAudio.Outputs().Find(x => x.Id == preferences.PhysicalOutputId);
        if (output == null) throw new InvalidOperationException("Выберите конечный аудиовыход.");
        if (preferences.AutoSwitchProfiles && preferences.DeviceRoutes.TryGetValue(output.Id, out var profile)) preferences.Route = profile;
        engine.Reset(); ParameterMapping.Apply(engine, State);
        systemAudio.Start(preferences.VirtualEndpointId, preferences.PhysicalOutputId, engine.Process);
        try { systemRoute.Start(preferences.VirtualEndpointId, preferences.PhysicalOutputId); systemRouteRestoreFailed = false; systemRoute.Watch(Environment.ProcessPath!); preferences.Save(); }
        catch { StopSystemAudio(); throw; }
        Notice(engine.MasterEnabled ? "Весь звук Windows направлен через ViPER" : "Звук Windows направлен через ViPER · включите «Обработка» для эффектов");
    }
    public void TryStartSystemAudio() { try { StartSystemAudio(); Render(); } catch (Exception e) { Notice(e.Message); } }
    void StopSystemAudio()
    {
        // Restore Windows while the processor is still alive to avoid routing sound into a stopped virtual sink.
        try { systemRoute.Restore(); systemRouteRestoreFailed = false; } catch (Exception e) { systemRouteRestoreFailed = true; Notice(e.Message); }
        systemAudio.Stop();
    }
    async void PrepareVirtualDevice()
    {
        string receipt = System.IO.Path.Combine(AppPreferences.DataPath, "virtual-prepare-" + Guid.NewGuid().ToString("N") + ".result.json");
        Directory.CreateDirectory(AppPreferences.DataPath);
        var endpoint = WindowsAudio.Outputs().Find(x => x.Id == preferences.VirtualEndpointId);
        var diagnosis = DriverSetup.Inspect(endpoint == null ? null : new OutputEndpoint(endpoint.Id, endpoint.Name, false));
        if (diagnosis.Registered && !diagnosis.IsAdmin) { await RunDriverAction(new[] { "--virtual-prepare", preferences.VirtualEndpointId, AppPreferences.DataPath, receipt }, receipt); return; }
        try { systemRoute.Prepare(preferences.VirtualEndpointId); Notice("В списке устройств Windows появилось имя ViPER."); Render(); }
        catch (COMException e) when (e.HResult == unchecked((int)0x80070005)) { await RunDriverAction(new[] { "--virtual-prepare", preferences.VirtualEndpointId, AppPreferences.DataPath, receipt }, receipt); }
        catch (Exception e) { Notice(e.Message); }
    }
    void RenderAudio()
    {
        RenderSystemAudio();
        var file = new StackPanel(); file.Children.Add(Text("Обработка WAV", 20, "Text", true)); file.Children.Add(Text("Применяет текущий пресет к аудиофайлу. Исходный файл сохраняется. Поддерживаются PCM и float, 44,1 / 48 кГц.", 13, "Muted"));
        var process = Button("Выбрать WAV и обработать…", async () =>
        {
            try
            {
                var open = new OpenFileDialog { Filter = "WAV|*.wav" }; if (open.ShowDialog() != true) return;
                var save = new SaveFileDialog { Filter = "WAV|*.wav", FileName = System.IO.Path.GetFileNameWithoutExtension(open.FileName) + "-ViPER.wav" }; if (save.ShowDialog() != true) return;
                if (System.IO.Path.GetFullPath(open.FileName).Equals(System.IO.Path.GetFullPath(save.FileName), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Выберите отдельный выходной файл.");
                var state = State.Clone(); Notice("Обработка файла…");
                await System.Threading.Tasks.Task.Run(() => { var data = WaveFile.Read(open.FileName); using var dsp = new DspEngine((uint)data.SampleRate); ParameterMapping.Apply(dsp, state); WaveFile.Process(open.FileName, save.FileName, dsp.Process, dsp.MasterEnabled ? (int)dsp.LatencyFrames : 0); });
                Notice("Сохранено: " + save.FileName);
            }
            catch (Exception e) { MessageBox.Show(e.Message, "Обработка WAV"); Notice(e.Message); }
        }, true); process.Margin = new Thickness(0, 18, 0, 0); process.HorizontalAlignment = HorizontalAlignment.Left; file.Children.Add(process); body.Children.Add(Panel(file));
        var stream = new StackPanel(); stream.Children.Add(Text("Вход → ViPER → выход", 20, "Text", true));
        stream.Children.Add(Text("Дополнительный режим для микрофона, линейного входа или записывающей стороны виртуального кабеля. Для звука приложений используйте устройство ViPER выше.", 13, "Muted"));
        var inputs = audio.Inputs; var outputs = audio.Outputs;
        stream.Children.Add(Text("Вход", 13, "Muted"));
        var inputNames = inputs.Select(x => x.Name + " [" + x.Id + "]").ToArray(); var outputNames = outputs.Select(x => x.Name + " [" + x.Id + "]").ToArray();
        if (inputs.Count > 0 && !inputs.Any(x => x.Id == preferences.InputDevice)) preferences.InputDevice = inputs[0].Id;
        if (outputs.Count > 0 && !outputs.Any(x => x.Id == preferences.OutputDevice)) preferences.OutputDevice = outputs[0].Id;
        var input = Combo(inputNames, inputs.Where(x => x.Id == preferences.InputDevice).Select(x => x.Name + " [" + x.Id + "]").FirstOrDefault() ?? "", val => { preferences.InputDevice = inputs[Array.IndexOf(inputNames, val)].Id; preferences.Save(); }); input.Margin = new Thickness(0, 8, 0, 16); input.HorizontalAlignment = HorizontalAlignment.Stretch; stream.Children.Add(input);
        stream.Children.Add(Text("Выход", 13, "Muted"));
        var output = Combo(outputNames, outputs.Where(x => x.Id == preferences.OutputDevice).Select(x => x.Name + " [" + x.Id + "]").FirstOrDefault() ?? "", val => { preferences.OutputDevice = outputs[Array.IndexOf(outputNames, val)].Id; preferences.Save(); }); output.Margin = new Thickness(0, 8, 0, 16); output.HorizontalAlignment = HorizontalAlignment.Stretch; stream.Children.Add(output);
        var actions = new WrapPanel(); actions.Children.Add(Button(audio.IsRunning ? "Остановить" : "Запустить поток", () => { if (audio.IsRunning) audio.Stop(); else { if (inputs.Count == 0 || outputs.Count == 0) throw new InvalidOperationException("Нужны входное и выходное устройства."); StopSystemAudio(); apo?.Disable(); apo?.Dispose(); apo = null; engine.Reset(); ParameterMapping.Apply(engine, State); audio.Start(preferences.InputDevice, preferences.OutputDevice, engine.Process); } Render(); }, true));
        actions.Children.Add(Button("Обновить устройства", Render)); stream.Children.Add(actions);
        stream.Children.Add(Text("48 кГц · стерео · блок 10 мс. Задержка и пропуски отображаются в состоянии драйвера.", 12, "Muted")); body.Children.Add(Panel(stream));
        var system = new StackPanel(); system.Children.Add(Text("Системный звук без виртуального кабеля", 19, "Text", true)); system.Children.Add(Text("Подключение через Windows APO — на странице «Драйвер».", 13, "Muted")); system.Children.Add(Button("Открыть подключение APO", () => { page = "Драйвер"; Render(); })); body.Children.Add(Panel(system));
    }
    void RenderDriver()
    {
        RenderSystemAudio();
        var info = new StackPanel(); info.Children.Add(Text("Звуковой движок", 20, "Text", true)); info.Children.Add(Text(engine.Version, 13, "Accent"));
        info.Children.Add(Text("Windows x64 · отдельный процессор ViPERDSP · 44,1 / 48 кГц · обработка стерео", 13, "Muted"));
        info.Children.Add(Text("Дополнительный входной поток: " + (audio.IsRunning ? "работает" : "остановлен") + "   ·   Кадров: " + audio.ProcessedFrames + "   ·   Пропуски: " + audio.DroppedBlocks, 13));
        info.Children.Add(Text("Очередь входного потока: " + audio.LatencyMilliseconds.ToString("0.0") + " мс", 13));
        apoStatusText = Text(ApoStatusDescription(), 13); info.Children.Add(apoStatusText);
        body.Children.Add(Panel(info));
        var diagnosis = DriverSetup.Inspect(CoreAudioProbe.DefaultOutput());
        var install = new StackPanel(); install.Children.Add(Text("Дополнительный режим через APO", 20, "Text", true));
        install.Children.Add(Text(systemAudio.IsRunning ? "Сейчас работает виртуальное устройство ViPER. Для настройки APO сначала остановите системный звук выше." : diagnosis.Summary, 14, "Muted"));
        install.Children.Add(Text("Назначьте драйвер текущему выходу, затем подключите пульт и включите «Обработка». Для назначения Windows запросит права администратора. Исходные настройки выхода сохраняются для отмены.\n\nПосле назначения закройте и заново откройте плеер. Если ответ не появился, нужна перезагрузка Windows. Статус подтверждается ответом драйвера и ростом счётчика кадров во время воспроизведения.", 13, "Muted"));
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), IsEnabled = !systemAudio.IsRunning };
        buttons.Children.Add(Button("Установщик APO", () => { string path = System.IO.Path.Combine(AppContext.BaseDirectory, "drivers", "ViPER4Windows_Setup.exe"); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }));
        var assign = Button(diagnosis.Registered ? "Обновить проверку" : "Назначить текущему выходу", () => { if (diagnosis.Registered) Render(); else RegisterCurrentOutput(); }); assign.IsEnabled = diagnosis.Installed && diagnosis.EndpointId != null; buttons.Children.Add(assign);
        buttons.Children.Add(Button("От администратора", () => { preferences.Save(); Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--wait-parent " + Environment.ProcessId + " --driver-page") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory }); Quit(); }));
        buttons.Children.Add(Button("Подключить APO", ConnectApo, true));
        buttons.Children.Add(Button("Отменить назначение", RestoreDriver));
        install.Children.Add(buttons); body.Children.Add(Panel(install));
        var fidelity = new StackPanel(); fidelity.Children.Add(Text("Соответствие Android-версии", 20, "Text", true));
        fidelity.Children.Add(Text("Из APK перенесены 18 разделов, 45 регуляторов, исходные значения, 12 EQ-пресетов, 23 профиля Dynamic System и XML-пресеты. Значки взяты из APK.\n\nВ отдельных тестах при 44,1 и 48 кГц EQ, DDC, Dynamic System, Spectrum Extension, Cure, Differential Surround, AnalogX и Natural/XHiFi Clarity совпали с восстановленным кодом 0.6.1 после учёта задержки.\n\nБас, Field Surround, реверберация, ламповый эффект и лимитер изменены; AGC, FET, свёртка и объём наушников доработаны. Сочетание эффектов также может звучать иначе из-за порядка обработки. Это близкая адаптация части алгоритмов, но полное совпадение звука Android не подтверждено. Подробные измерения — verification/fidelity-audit.md.", 14, "Muted"));
        var links = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) }; links.Children.Add(Button("Исходный проект", () => OpenUrl("https://github.com/AndroidAudioMods/ViPER4Android"))); links.Children.Add(Button("ViPERDSP", () => OpenUrl("https://github.com/likelikeslike/ViPERDSP"))); links.Children.Add(Button("Драйвер Windows", () => OpenUrl("https://github.com/likelikeslike/ViPER4Windows/releases/tag/2.0.1"))); fidelity.Children.Add(links); body.Children.Add(Panel(fidelity));
    }
    string ApoStatusDescription()
    {
        var s = apo?.ReadStatus() ?? (false, 0, 0L, "");
        bool recentProgress = apoProgressTick != 0 && Environment.TickCount64 - apoProgressTick < 3000;
        return "APO: " + (s.Item1 ? $"есть ответ драйвера · {(!engine.MasterEnabled ? "обработка выключена" : recentProgress ? "звук обрабатывается" : "ожидаю воспроизведение")} · {s.Item2} Гц · {s.Item3:N0} кадров" : apo?.Connected == true ? "пульт подключён; ответа драйвера пока нет" : "пульт не подключён");
    }
    void ConnectApo()
    {
        var d = DriverSetup.Inspect(CoreAudioProbe.DefaultOutput());
        if (!d.Installed || !d.Registered || d.EffectsDisabled) throw new InvalidOperationException(d.Summary);
        StopSystemAudio(); audio.Stop(); apo?.Dispose(); apo = new ApoBridge();
        if (!apo.Connected) throw new InvalidOperationException("Не удалось открыть пульт APO. " + (apo.NativeErrorCode == 5 ? "Нажмите «От администратора»: Windows запрещает создавать канал драйвера без этих прав.\n" : "\n") + apo.Error);
        apoLastFrames = apo.ReadStatus().Frames; apoProgressTick = 0;
        PublishApo(); Notice("Пульт подключён. Запустите воспроизведение для проверки ответа драйвера."); Render();
    }
    public void TryConnectApo() { try { ConnectApo(); } catch (Exception e) { Notice(e.Message); } }
    async void RegisterCurrentOutput()
    {
        if (systemAudio.IsRunning) { Notice("Сначала остановите системный звук: виртуальный ViPER уже обрабатывает звук."); return; }
        var endpoint = CoreAudioProbe.DefaultOutput(); if (endpoint == null) { Notice("Windows не сообщает текущий звуковой выход."); return; }
        string backups = System.IO.Path.Combine(AppPreferences.DataPath, "driver-backups"); Directory.CreateDirectory(backups);
        string receipt = System.IO.Path.Combine(backups, "registration-" + Guid.NewGuid().ToString("N") + ".result.json");
        await RunDriverAction(new[] { "--driver-register", endpoint.Id, backups, receipt }, receipt);
    }
    async void RestoreDriver()
    {
        var dialog = new OpenFileDialog { Filter = "Резервная копия выхода (*.json)|*.json", InitialDirectory = System.IO.Path.Combine(AppPreferences.DataPath, "driver-backups"), Title = "Выберите резервную копию звукового выхода" };
        if (dialog.ShowDialog() != true) return;
        apo?.Disable(); apo?.Dispose(); apo = null;
        string receipt = System.IO.Path.Combine(AppPreferences.DataPath, "driver-restore-" + Guid.NewGuid().ToString("N") + ".result.json");
        await RunDriverAction(new[] { "--driver-restore", dialog.FileName, receipt }, receipt);
    }
    async System.Threading.Tasks.Task RunDriverAction(string[] arguments, string receipt)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
            Notice("Ожидаю подтверждение Windows и результат назначения…");
            using var process = Process.Start(start); if (process != null) await process.WaitForExitAsync();
            if (!File.Exists(receipt)) throw new InvalidOperationException("Windows не вернул результат назначения.");
            using var result = JsonDocument.Parse(File.ReadAllText(receipt));
            if (!result.RootElement.GetProperty("Success").GetBoolean()) throw new InvalidOperationException(result.RootElement.GetProperty("Error").GetString());
            Notice(result.RootElement.GetProperty("Result").GetString() ?? "Готово. Откройте плеер заново."); Render();
        }
        catch (Exception e) { Notice(e.Message); }
    }
    void RenderSettings()
    {
        var p = new StackPanel(); p.Children.Add(Text("Приложение", 20, "Text", true));
        var theme = new CheckBox { Content = "Светлая тема", IsChecked = preferences.LightTheme, Margin = new Thickness(0, 20, 0, 16) }; theme.Click += (_, _) => { preferences.LightTheme = theme.IsChecked == true; ApplyTheme(); preferences.Save(); Render(); }; p.Children.Add(theme);
        var toTray = new CheckBox { Content = "Сворачивать в трей при закрытии окна", IsChecked = preferences.CloseToTray, Margin = new Thickness(0, 0, 0, 18) }; toTray.Click += (_, _) => { preferences.CloseToTray = toTray.IsChecked == true; preferences.Save(); }; p.Children.Add(toTray);
        var automatic = new CheckBox { Content = "Отдельный профиль для каждого выхода Windows", IsChecked = preferences.AutoSwitchProfiles, Margin = new Thickness(0, 0, 0, 18) }; automatic.Click += (_, _) => { preferences.AutoSwitchProfiles = automatic.IsChecked == true; endpointId = ""; if (preferences.AutoSwitchProfiles) { SyncEndpointProfile(); Changed(); Render(); } preferences.Save(); }; p.Children.Add(automatic);
        var startup = new CheckBox { Content = "Запускать вместе с Windows", IsChecked = StartupEnabled(), Margin = new Thickness(0, 0, 0, 18) }; startup.Click += (_, _) => { using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (startup.IsChecked == true) key.SetValue("ViPER4Windows-RE", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("ViPER4Windows-RE", false); }; p.Children.Add(startup);
        p.Children.Add(Text("Автозапуск открывает пульт. Автоматическую обработку устройства ViPER можно включить на странице «Звук».", 12, "Muted")); body.Children.Add(Panel(p));
        var data = new StackPanel(); data.Children.Add(Text("Данные и резервная копия", 20, "Text", true)); data.Children.Add(Text("Настройки и пресеты хранятся рядом с приложением, в папке data. Для переноса на другой ПК скопируйте всю папку.", 13, "Muted"));
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) }; buttons.Children.Add(Button("Открыть папку данных", () => { Directory.CreateDirectory(AppPreferences.DataPath); Process.Start(new ProcessStartInfo(AppPreferences.DataPath) { UseShellExecute = true }); }));
        buttons.Children.Add(Button("Сбросить текущий выход", () => { if (MessageBox.Show("Сбросить все настройки «" + preferences.Route + "»?", "Сброс", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { State.Reset(catalog); Changed(); Render(); } })); data.Children.Add(buttons); body.Children.Add(Panel(data));
        var about = new StackPanel(); about.Children.Add(Text("ViPER4Windows-RE", 20, "Text", true)); about.Children.Add(Text("Локальная адаптация ViPER4Android-RE 0.6.2 для Windows.\nАвторы исходного ViPER: Zhuhang и ViPER520. Восстановление: Martmists, Iscle и likelikeslike.\nКод ViPERDSP предназначен для личного некоммерческого использования. Подробности — THIRD-PARTY-NOTICES.txt и исходники в комплекте.", 13, "Muted"));
        about.Children.Add(Button("Закрыть приложение", Quit)); body.Children.Add(Panel(about));
    }
    bool StartupEnabled() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("ViPER4Windows-RE") != null; }
    void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    void Changed()
    {
        try
        {
            ParameterMapping.Apply(engine, State);
            if (apo?.Connected == true)
            {
                PublishApo();
            }
            Notice(engine.MasterEnabled ? systemAudio.IsRunning ? "Звук Windows обрабатывается через виртуальный ViPER" : audio.IsRunning ? "Обработка входного потока включена" : apo?.ReadStatus().Configured == true ? "Обработка через APO включена" : "Пресет активен · выберите источник на странице «Звук»" : systemAudio.IsRunning ? "Эффекты выключены · звук проходит через ViPER" : "Обработка выключена");
            saveTimer.Stop(); saveTimer.Start();
        }
        catch (Exception e) { Notice(e.Message); }
    }
    void Poll()
    {
        if (systemRoute.Active && !systemAudio.IsRunning && !systemRouteRestoreFailed) { StopSystemAudio(); Render(); }
        if (++pollCount % 6 == 0 && preferences.AutoSwitchProfiles && !audio.IsRunning && !systemAudio.IsRunning && SyncEndpointProfile()) { Changed(); Render(); }
        if (apo?.Connected == true)
        {
            var response = apo.ReadStatus(); int rate = response.SampleRate;
            if (response.Frames != apoLastFrames) { if (response.Frames > apoLastFrames) apoProgressTick = Environment.TickCount64; apoLastFrames = response.Frames; }
            if (rate is 44100 or 48000 && (apoRateEngine == null || apoRateEngine.SampleRate != rate)) { try { PublishApo(); } catch (Exception e) { Notice(e.Message); } }
        }
        if (systemAudio.IsRunning) meters.Text = $"ViPER  IN {systemAudio.InputPeak:0.000}  OUT {systemAudio.OutputPeak:0.000}  ·  {systemAudio.QueueMilliseconds + systemAudio.DeviceLatencyMilliseconds:0} мс";
        else if (audio.IsRunning) meters.Text = $"IN {audio.InputPeak:0.00}  OUT {audio.OutputPeak:0.00}  ·  {audio.LatencyMilliseconds:0} мс";
        else if (apo?.ReadStatus() is var s && s?.Configured == true) meters.Text = $"APO  {s.Value.SampleRate / 1000.0:0.#} кГц  ·  {s.Value.Frames:N0} кадров";
        else meters.Text = "СТЕРЕО  ·  48 кГц";
        if (page == "Драйвер" && apoStatusText != null) apoStatusText.Text = ApoStatusDescription();
        if (systemStatusText != null) systemStatusText.Text = SystemStatus();
        if (systemAudio.IsRunning && pollCount % 3 == 0)
        {
            try { File.WriteAllText(System.IO.Path.Combine(AppPreferences.DataPath, "system-audio-status.json"), JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow, systemAudio.IsRunning, systemAudio.RawOutputEnabled, Source = preferences.VirtualEndpointId, Output = preferences.PhysicalOutputId, EffectsEnabled = engine.MasterEnabled, systemAudio.ProcessedFrames, systemAudio.RenderedFrames, systemAudio.InputPeak, systemAudio.OutputPeak, systemAudio.QueueMilliseconds, systemAudio.DeviceLatencyMilliseconds, systemAudio.Discontinuities, systemAudio.DroppedFrames, systemAudio.Underruns, systemAudio.Error })); } catch (IOException) { }
        }
    }
    void PublishApo()
    {
        if (apo?.Connected != true) return;
        int rate = apo.ReadStatus().SampleRate;
        if (rate != 44100 && rate != 48000) rate = 48000;
        if (apoRateEngine == null || apoRateEngine.SampleRate != rate) { apoRateEngine?.Dispose(); apoRateEngine = new DspEngine((uint)rate); }
        ParameterMapping.Apply(apoRateEngine, State);
        apo.Publish(apoRateEngine);
    }
    bool SyncEndpointProfile()
    {
        var endpoint = CoreAudioProbe.DefaultOutput();
        if (endpoint == null || endpoint.Id == endpointId) return false;
        endpointId = endpoint.Id;
        if (!preferences.DeviceRoutes.TryGetValue(endpoint.Id, out string? route))
        {
            route = endpoint.Name;
            if (preferences.Routes.ContainsKey(route)) route += " · " + endpoint.Id.GetHashCode().ToString("X8");
            preferences.DeviceRoutes[endpoint.Id] = route; preferences.Routes[route] = State.Clone();
        }
        preferences.Route = route;
        return true;
    }
    void Notice(string text) { lastNotice = text; status.Text = text; }
    public void Quit() { quitting = true; Close(); }
    public void RunUiSmokeTest(string folder)
    {
        Directory.CreateDirectory(folder); bool original = preferences.LightTheme;
        foreach (bool light in new[] { false, true })
        {
            preferences.LightTheme = light; ApplyTheme();
            foreach (var name in new[] { "Эффекты", "Пресеты", "Звук", "Драйвер", "Настройки" })
            {
                page = name; Render(); UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(this);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp)); using var stream = File.Create(System.IO.Path.Combine(folder, (light ? "light-" : "dark-") + name + ".png")); png.Save(stream);
            }
        }
        preferences.LightTheme = original; ApplyTheme(); page = "Эффекты"; Render();
        foreach (var fx in catalog.Effects) expanded.Add(fx.Key);
        Render(); UpdateLayout();
        contentScroll!.ScrollToVerticalOffset(650); UpdateLayout(); double before = contentScroll.VerticalOffset;
        Render(); UpdateLayout(); double after = contentScroll!.VerticalOffset;
        if (before < 600 || Math.Abs(after - before) > 1) throw new InvalidOperationException("Перестроение страницы сбросило положение списка.");
        var card = body.Children.OfType<Border>().First(x => x.Tag is EffectDef fx && fx.Key == "65574");
        var container = (StackPanel)card.Child; var header = (Grid)container.Children[0]; var title = (StackPanel)header.Children[1];
        title.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        UpdateLayout(); double afterClick = contentScroll.VerticalOffset;
        if (Math.Abs(afterClick - before) > 1) throw new InvalidOperationException("Нажатие на эффект сбросило положение списка.");
        expanded.Clear(); expanded.Add("36868"); expanded.Add("65551"); Render();
        File.WriteAllText(System.IO.Path.Combine(folder, "ui-tests.json"), JsonSerializer.Serialize(new { passed = true, pages = 5, themes = 2, allEffectCardsRendered = 18, repeatRender = true, scrollBefore = before, scrollAfterRender = after, scrollAfterEffectClick = afterClick }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class EqPlot : FrameworkElement
{
    readonly float[] gains; readonly Brush accent, line, muted;
    public EqPlot(float[] gains, Brush accent, Brush line, Brush muted) { this.gains = gains; this.accent = accent; this.line = line; this.muted = muted; }
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 10) return;
        for (int i = 0; i <= 4; i++) dc.DrawLine(new Pen(line, 1), new Point(0, i * h / 4), new Point(w, i * h / 4));
        var geometry = new StreamGeometry(); using (var g = geometry.Open()) { for (int i = 0; i < 10; i++) { var p = new Point(w * i / 9, h / 2 - gains[i] * (h - 10) / 24); if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, false); } }
        dc.DrawGeometry(null, new Pen(accent, 2), geometry);
        for (int i = 0; i < 10; i++) dc.DrawEllipse(accent, null, new Point(w * i / 9, h / 2 - gains[i] * (h - 10) / 24), 3, 3);
    }
}
