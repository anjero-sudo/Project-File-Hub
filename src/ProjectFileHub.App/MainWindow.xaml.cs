using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ProjectFileHub.App.ViewModels;
using ProjectFileHub.App.Diagnostics;
using ProjectFileHub.App.WindowsIntegration;
using ProjectFileHub.Core;
using ProjectFileHub.Core.Models;
using ProjectFileHub.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;
using FontWeight = Windows.UI.Text.FontWeight;

namespace ProjectFileHub.App;

public sealed partial class MainWindow : Window
{
    private enum PreviewMode
    {
        WorkspaceQuickPreview,
        SingleQuickLook
    }

    private readonly FileSystemBrowser _fileBrowser = new();
    private readonly FileOperationService _fileOperations = new();
    private readonly RecycleBinService _recycleBin;
    private readonly ProjectRegistryStore _registryStore;
    private readonly AppSettingsStore _settingsStore;
    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly bool _launchToTray;
    private readonly DispatcherTimer _treeHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly Dictionary<string, string> _pendingExtensionRenames = new(StringComparer.OrdinalIgnoreCase);
    private ProjectRegistryState _registryState = new();
    private AppSettingsState _settingsState = new();
    private RegisteredProject? _activeProject;
    private ExplorerItemViewModel? _selectedItem;
    private ExplorerItemViewModel? _previewItem;
    private string? _currentFolder;
    private FileSortField _sortField = FileSortField.Name;
    private SortDirection _sortDirection = SortDirection.Ascending;
    private FileItemCategory? _categoryFilter;
    private bool _loaded;
    private bool _synchronizingProjectPicker;
    private bool _synchronizingSelection;
    private bool _synchronizingPreview;
    private bool _synchronizingWorkspaceControls;
    private bool _multiSelectMode;
    private bool _renameCommitInProgress;
    private int _previewVersion;
    private IReadOnlyList<string> _draggedPaths = [];
    private string? _hoveredTreePath;
    private Func<Task>? _undoAction;
    private bool _undoInProgress;
    private ProjectIndexService? _projectIndex;
    private Task? _indexInitialization;
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _fileViewCancellation;
    private int _fileViewVersion;
    private PreviewMode _previewMode = PreviewMode.WorkspaceQuickPreview;
    private bool _previewTextWrapEnabled = true;
    private float _previewZoomFactor = 1.0f;
    private MinimumWindowSizeService? _minimumWindowSizeService;
    private AppWindow? _appWindow;
    private NotificationAreaService? _notificationAreaService;
    private bool _applicationExitRequested;
    private bool _isHidingToTray;
    private bool _isPreviewImagePanning;
    private uint _previewImagePanPointerId;
    private double _previewImagePanStartX;
    private double _previewImagePanStartY;
    private double _previewImagePanStartHorizontalOffset;
    private double _previewImagePanStartVerticalOffset;

    private const float PreviewZoomMinimum = 0.5f;
    private const float PreviewZoomMaximum = 8.0f;
    private const double PreviewZoomStep = 1.15;

    private bool IsNotificationAreaEnabled =>
        _notificationAreaService is not null && _settingsState.EffectiveCloseToTrayEnabled;

    private static readonly Regex MarkdownInlineRegex = new(
        @"(\*\*[^*\r\n]+\*\*|__[^_\r\n]+__|`[^`\r\n]+`|\[[^\]\r\n]+\]\([^)\r\n]+\)|\*[^*\r\n]+\*|_[^_\r\n]+_)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly SolidColorBrush CodePlainBrush = new(ColorHelper.FromArgb(255, 248, 248, 242));
    private static readonly SolidColorBrush CodeCommentBrush = new(ColorHelper.FromArgb(255, 117, 113, 94));
    private static readonly SolidColorBrush CodeStringBrush = new(ColorHelper.FromArgb(255, 230, 219, 116));
    private static readonly SolidColorBrush CodeNumberBrush = new(ColorHelper.FromArgb(255, 174, 129, 255));
    private static readonly SolidColorBrush CodeKeywordBrush = new(ColorHelper.FromArgb(255, 249, 38, 114));

    public MainWindow(bool launchToTray = false)
    {
        _launchToTray = launchToTray;
        AppDiagnostics.Log("MainWindow constructor entered");
        InitializeComponent();
        AttachPreviewToRootHost();
        RootLayout.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        PreviewImageScroll.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewImagePointerWheelChanged),
            true);
        PreviewImageCanvas.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnPreviewImagePointerPressed),
            true);
        PreviewImageCanvas.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnPreviewImagePointerMoved),
            true);
        PreviewImageCanvas.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnPreviewImagePointerReleased),
            true);
        PreviewImageCanvas.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnPreviewImagePointerCanceled),
            true);
        PreviewImageCanvas.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnPreviewImagePointerCaptureLost),
            true);
        PreviewImageScroll.SizeChanged += OnPreviewImageScrollSizeChanged;
        AppDiagnostics.Log("MainWindow.InitializeComponent completed");

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _registryStore = new ProjectRegistryStore(
            Path.Combine(localData, "ProjectFileHub", "projects.json"),
            Path.Combine(roamingData, "Anjero", "ProjectFileHub", "projects.backup.json"));
        _settingsStore = new AppSettingsStore(
            Path.Combine(localData, "ProjectFileHub", "settings.json"));
        _recycleBin = new RecycleBinService(_fileOperations);
        _treeHoverTimer.Tick += OnTreeHoverTimerTick;
        Closed += OnWindowClosed;

        ConfigureWindow();
        AppDiagnostics.Log("MainWindow configured");
    }

    private void AttachPreviewToRootHost()
    {
        WorkspaceGrid.Children.Remove(SinglePreviewScrim);
        WorkspaceGrid.Children.Remove(PreviewOverlay);

        Grid.SetColumn(SinglePreviewScrim, 0);
        Grid.SetColumnSpan(SinglePreviewScrim, 1);
        Grid.SetRow(SinglePreviewScrim, 1);
        Grid.SetRowSpan(SinglePreviewScrim, 2);
        RootLayout.Children.Add(SinglePreviewScrim);

        Grid.SetColumn(PreviewOverlay, 0);
        Grid.SetColumnSpan(PreviewOverlay, 1);
        Grid.SetRow(PreviewOverlay, 1);
        Grid.SetRowSpan(PreviewOverlay, 2);
        PreviewOverlay.Margin = new Thickness(0);
        PreviewOverlay.MaxWidth = double.PositiveInfinity;
        PreviewOverlay.MaxHeight = double.PositiveInfinity;
        PreviewOverlay.CornerRadius = new CornerRadius(0);
        PreviewOverlay.BorderThickness = new Thickness(0, 1, 0, 0);
        RootLayout.Children.Add(PreviewOverlay);
    }

    public ObservableCollection<ExplorerItemViewModel> Items { get; } = [];

    public ObservableCollection<ExplorerItemViewModel> PreviewItems { get; } = [];

    private void ConfigureWindow()
    {
        Title = "Project File Hub";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var windowHandle = WindowNative.GetWindowHandle(this);
        _minimumWindowSizeService = new MinimumWindowSizeService(windowHandle, 800, 800);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        appWindow.Closing += OnAppWindowClosing;
        appWindow.Changed += OnAppWindowChanged;
        appWindow.Resize(new SizeInt32(1480, 900));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ProjectFileHub.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
            try
            {
                _notificationAreaService = new NotificationAreaService(
                    iconPath,
                    () => DispatcherQueue.TryEnqueue(RestoreFromTray),
                    () => DispatcherQueue.TryEnqueue(ToggleProjectIndexFromTray),
                    () => DispatcherQueue.TryEnqueue(RequestApplicationExit),
                    GetNotificationAreaSnapshot);
                AppDiagnostics.Log("Notification area initialized");
            }
            catch (Exception exception)
            {
                AppDiagnostics.Log("Notification area initialization failed", exception);
            }
        }

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 205, 217, 230);
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 21, 38, 58);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 11, 49, 82);
        }
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        AppDiagnostics.Log("Root loaded; settings reload starting");
        await ReloadSettingsAsync();
        AppDiagnostics.Log("Root loaded; settings reload completed");
        AppDiagnostics.Log("Root loaded; registry reload starting");
        await ReloadRegistryAsync();
        AppDiagnostics.Log("Root loaded; registry reload completed");
        AppDiagnostics.MarkStartupStable();

        if (_launchToTray && IsNotificationAreaEnabled)
        {
            HideWindowToTray(showTip: false);
        }
    }

    private async Task ReloadSettingsAsync()
    {
        try
        {
            _settingsState = await _settingsStore.LoadAsync();
            ApplyApplicationSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _settingsState = new AppSettingsState();
            ApplyApplicationSettings();
            SetStatus($"设置文件无法读取，已使用默认设置：{exception.Message}");
        }
    }

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        ClosePreview();
        SettingsSpacePreviewSwitch.IsOn = _settingsState.SpacePreviewEnabled;
        SettingsInspectorSwitch.IsOn = _settingsState.InspectorVisible;
        SettingsFilterRailSwitch.IsOn = _settingsState.FilterRailVisible;
        SettingsRememberWorkspaceSwitch.IsOn = _settingsState.RestoreWorkspace;
        SettingsStartupSwitch.IsOn = _settingsState.StartWithWindows;
        SettingsTraySwitch.IsOn = _settingsState.EffectiveCloseToTrayEnabled;
        SelectSettingsOption(SettingsThemePicker, _settingsState.Theme);
        SelectSettingsOption(SettingsDensityPicker, _settingsState.Density);
        SettingsStatusText.Text = "设置保存在本机，不写入项目目录";
        SettingsStatusText.Foreground = (Brush)Application.Current.Resources["HubTextMutedBrush"];
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void OnCloseSettingsClicked(object sender, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    private async void OnSaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        var theme = GetSettingsOption(SettingsThemePicker, AppThemeNames.Midnight);
        var density = GetSettingsOption(SettingsDensityPicker, AppDensityNames.Comfortable);
        var startWithWindows = SettingsStartupSwitch.IsOn;

        try
        {
            if (startWithWindows != _settingsState.StartWithWindows)
            {
                _startupRegistration.SetEnabled(startWithWindows);
            }

            var workspaces = SettingsRememberWorkspaceSwitch.IsOn
                ? new Dictionary<Guid, ProjectWorkspaceState>(_settingsState.ProjectWorkspaces)
                : new Dictionary<Guid, ProjectWorkspaceState>();
            _settingsState = _settingsState with
            {
                SpacePreviewEnabled = SettingsSpacePreviewSwitch.IsOn,
                InspectorVisible = SettingsInspectorSwitch.IsOn,
                FilterRailVisible = SettingsFilterRailSwitch.IsOn,
                RestoreWorkspace = SettingsRememberWorkspaceSwitch.IsOn,
                StartWithWindows = startWithWindows,
                CloseToTrayEnabled = SettingsTraySwitch.IsOn,
                CloseToTrayConfigured = true,
                Theme = theme,
                Density = density,
                ProjectWorkspaces = workspaces
            };

            ApplyApplicationSettings();
            if (_settingsState.RestoreWorkspace)
            {
                UpdateCurrentWorkspaceSnapshot();
            }

            await _settingsStore.SaveAsync(_settingsState);
            SettingsOverlay.Visibility = Visibility.Collapsed;
            SetStatus("设置已保存");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SettingsStatusText.Text = $"设置未保存：{exception.Message}";
            SettingsStatusText.Foreground = (Brush)Application.Current.Resources["HubDangerBrush"];
        }
    }

    private static void SelectSettingsOption(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.Ordinal));
        comboBox.SelectedIndex = comboBox.SelectedItem is null ? 0 : comboBox.SelectedIndex;
    }

    private static string GetSettingsOption(ComboBox comboBox, string fallback) =>
        comboBox.SelectedItem is ComboBoxItem { Tag: string value } ? value : fallback;

    private void ApplyApplicationSettings()
    {
        ApplyTheme(_settingsState.Theme);
        ApplyDensity(_settingsState.Density);
        InspectorPanel.Visibility = _settingsState.InspectorVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        TypeFilterRail.Visibility = _settingsState.FilterRailVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        SpacePreviewHint.Visibility = _settingsState.SpacePreviewEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_notificationAreaService is not null)
        {
            _notificationAreaService.IsVisible = _settingsState.EffectiveCloseToTrayEnabled;
            AppDiagnostics.Log(
                $"Notification area visibility applied · visible={_notificationAreaService.IsVisible}");
        }

        if (!_settingsState.FilterRailVisible && _categoryFilter is not null)
        {
            _categoryFilter = null;
            SetSelectedFilterControl(null);
            UpdateActiveFilterState();
            UpdateFolderHeader();
            if (_activeProject is not null)
            {
                RefreshFileView();
                ScheduleWorkspaceSave();
            }
        }
    }

    private void ApplyDensity(string density)
    {
        var (treeWidth, filterWidth, inspectorWidth, horizontalPadding) = density switch
        {
            AppDensityNames.Compact => (208d, 70d, 260d, 12d),
            AppDensityNames.Spacious => (264d, 86d, 324d, 24d),
            _ => (232d, 78d, 292d, 18d)
        };

        TreeColumn.Width = new GridLength(treeWidth);
        FilterRailColumn.Width = _settingsState.FilterRailVisible
            ? new GridLength(filterWidth)
            : new GridLength(0);
        InspectorColumn.Width = _settingsState.InspectorVisible
            ? new GridLength(inspectorWidth)
            : new GridLength(0);
        MainFilePanel.Padding = new Thickness(horizontalPadding, 14, horizontalPadding, 10);
    }

    private void ApplyTheme(string theme)
    {
        var light = string.Equals(theme, AppThemeNames.Light, StringComparison.Ordinal);
        RootLayout.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;

        if (light)
        {
            SetBrushColor("HubCanvasBrush", 0xF4, 0xF7, 0xFB);
            SetBrushColor("HubPanelBrush", 0xFF, 0xFF, 0xFF);
            SetBrushColor("HubRaisedBrush", 0xE8, 0xEF, 0xF6);
            SetBrushColor("HubHoverBrush", 0xD9, 0xE8, 0xF4);
            SetBrushColor("HubSelectedBrush", 0xCF, 0xEA, 0xFA);
            SetBrushColor("HubAccentBrush", 0x02, 0x84, 0xC7);
            SetBrushColor("HubAccentStrongBrush", 0x03, 0x69, 0xA1);
            SetBrushColor("HubTextBrush", 0x10, 0x20, 0x33);
            SetBrushColor("HubTextSecondaryBrush", 0x42, 0x54, 0x6A);
            SetBrushColor("HubTextMutedBrush", 0x64, 0x74, 0x8B);
            SetBrushColor("HubBorderBrush", 0xC8, 0xD5, 0xE2);
            SetBrushColor("HubDangerBrush", 0xC2, 0x41, 0x4D);
            SetBrushColor("HubSuccessBrush", 0x16, 0x88, 0x66);
        }
        else if (string.Equals(theme, AppThemeNames.Graphite, StringComparison.Ordinal))
        {
            SetBrushColor("HubCanvasBrush", 0x0B, 0x0E, 0x12);
            SetBrushColor("HubPanelBrush", 0x11, 0x16, 0x1C);
            SetBrushColor("HubRaisedBrush", 0x18, 0x20, 0x28);
            SetBrushColor("HubHoverBrush", 0x22, 0x2D, 0x38);
            SetBrushColor("HubSelectedBrush", 0x14, 0x38, 0x4B);
            SetBrushColor("HubAccentBrush", 0x20, 0xA4, 0xD6);
            SetBrushColor("HubAccentStrongBrush", 0x35, 0xC2, 0xF2);
            SetBrushColor("HubTextBrush", 0xF2, 0xF5, 0xF7);
            SetBrushColor("HubTextSecondaryBrush", 0xAD, 0xB7, 0xC2);
            SetBrushColor("HubTextMutedBrush", 0x75, 0x83, 0x91);
            SetBrushColor("HubBorderBrush", 0x2A, 0x36, 0x42);
            SetBrushColor("HubDangerBrush", 0xEF, 0x72, 0x7B);
            SetBrushColor("HubSuccessBrush", 0x4A, 0xC8, 0x9D);
        }
        else
        {
            SetBrushColor("HubCanvasBrush", 0x07, 0x10, 0x1B);
            SetBrushColor("HubPanelBrush", 0x0B, 0x15, 0x21);
            SetBrushColor("HubRaisedBrush", 0x10, 0x1C, 0x29);
            SetBrushColor("HubHoverBrush", 0x15, 0x26, 0x3A);
            SetBrushColor("HubSelectedBrush", 0x0B, 0x31, 0x52);
            SetBrushColor("HubAccentBrush", 0x0E, 0xA5, 0xE9);
            SetBrushColor("HubAccentStrongBrush", 0x19, 0xB5, 0xFE);
            SetBrushColor("HubTextBrush", 0xF2, 0xF6, 0xFA);
            SetBrushColor("HubTextSecondaryBrush", 0xA7, 0xB3, 0xC3);
            SetBrushColor("HubTextMutedBrush", 0x70, 0x80, 0x96);
            SetBrushColor("HubBorderBrush", 0x20, 0x30, 0x44);
            SetBrushColor("HubDangerBrush", 0xEF, 0x6A, 0x75);
            SetBrushColor("HubSuccessBrush", 0x45, 0xC4, 0x9A);
        }

        ApplyFileVisualTheme(light);

        ApplyTitleBarTheme(light);
    }

    private static void ApplyFileVisualTheme(bool light)
    {
        if (light)
        {
            SetBrushColor("HubFileFolderBrush", 0x03, 0x67, 0x8F);
            SetBrushColor("HubFileImageBrush", 0x0F, 0x76, 0x6E);
            SetBrushColor("HubFileVideoBrush", 0x5B, 0x4B, 0xC0);
            SetBrushColor("HubFileAudioBrush", 0xB8, 0x32, 0x80);
            SetBrushColor("HubFilePdfBrush", 0xB8, 0x32, 0x3D);
            SetBrushColor("HubFileWordBrush", 0x1D, 0x4E, 0xD8);
            SetBrushColor("HubFileSpreadsheetBrush", 0x0F, 0x76, 0x6E);
            SetBrushColor("HubFilePresentationBrush", 0x9A, 0x45, 0x07);
            SetBrushColor("HubFileMarkdownBrush", 0x03, 0x69, 0xA1);
            SetBrushColor("HubFileTextBrush", 0x52, 0x62, 0x78);
            SetBrushColor("HubFileCodeBrush", 0x8A, 0x5B, 0x00);
            SetBrushColor("HubFileDataBrush", 0x0F, 0x76, 0x6E);
            SetBrushColor("HubFileDatabaseBrush", 0x7C, 0x3A, 0xED);
            SetBrushColor("HubFileArchiveBrush", 0x8A, 0x52, 0x06);
            SetBrushColor("HubFileExecutableBrush", 0x1D, 0x4E, 0xD8);
            SetBrushColor("HubFileFontBrush", 0xBE, 0x18, 0x5D);
            SetBrushColor("HubFileDocumentBrush", 0x47, 0x55, 0x69);
            SetBrushColor("HubFileOtherBrush", 0x5B, 0x6B, 0x80);
            return;
        }

        SetBrushColor("HubFileFolderBrush", 0x19, 0xB5, 0xFE);
        SetBrushColor("HubFileImageBrush", 0x2D, 0xD4, 0xBF);
        SetBrushColor("HubFileVideoBrush", 0xA7, 0x8B, 0xFA);
        SetBrushColor("HubFileAudioBrush", 0xF4, 0x72, 0xB6);
        SetBrushColor("HubFilePdfBrush", 0xFF, 0x70, 0x7A);
        SetBrushColor("HubFileWordBrush", 0x60, 0xA5, 0xFA);
        SetBrushColor("HubFileSpreadsheetBrush", 0x45, 0xC4, 0x9A);
        SetBrushColor("HubFilePresentationBrush", 0xF5, 0xA4, 0x5D);
        SetBrushColor("HubFileMarkdownBrush", 0x56, 0xC2, 0xFF);
        SetBrushColor("HubFileTextBrush", 0xA7, 0xB3, 0xC3);
        SetBrushColor("HubFileCodeBrush", 0xE4, 0xC6, 0x6F);
        SetBrushColor("HubFileDataBrush", 0x4D, 0xD8, 0xD0);
        SetBrushColor("HubFileDatabaseBrush", 0xB9, 0x9A, 0xFD);
        SetBrushColor("HubFileArchiveBrush", 0xF4, 0xB8, 0x60);
        SetBrushColor("HubFileExecutableBrush", 0x74, 0xC0, 0xFC);
        SetBrushColor("HubFileFontBrush", 0xF0, 0x9A, 0xB3);
        SetBrushColor("HubFileDocumentBrush", 0x94, 0xA3, 0xB8);
        SetBrushColor("HubFileOtherBrush", 0x70, 0x80, 0x96);
    }

    private static void SetBrushColor(string resourceKey, byte red, byte green, byte blue)
    {
        if (Application.Current.Resources[resourceKey] is SolidColorBrush brush)
        {
            brush.Color = ColorHelper.FromArgb(255, red, green, blue);
        }
    }

    private void ApplyTitleBarTheme(bool light)
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = light
            ? ColorHelper.FromArgb(255, 16, 32, 51)
            : ColorHelper.FromArgb(255, 205, 217, 230);
        titleBar.ButtonHoverBackgroundColor = light
            ? ColorHelper.FromArgb(255, 217, 232, 244)
            : ColorHelper.FromArgb(255, 21, 38, 58);
        titleBar.ButtonHoverForegroundColor = light
            ? ColorHelper.FromArgb(255, 3, 105, 161)
            : Colors.White;
        titleBar.ButtonPressedBackgroundColor = light
            ? ColorHelper.FromArgb(255, 207, 234, 250)
            : ColorHelper.FromArgb(255, 11, 49, 82);
    }

    private async Task ReloadRegistryAsync()
    {
        try
        {
            _registryState = await _registryStore.LoadAsync();
            var recoveredRegistry = _registryStore.LastLoadRecoveredFromBackup;
            AppDiagnostics.Log($"Registry loaded · projects={_registryState.Projects.Count} · active={_registryState.ActiveProject?.RootPath ?? "none"}");
            if (recoveredRegistry)
            {
                AppDiagnostics.Log("Project registry recovered from a redundant copy; primary repair attempted");
            }

            _synchronizingProjectPicker = true;
            ProjectPicker.ItemsSource = _registryState.Projects;
            ProjectPicker.SelectedItem = AppDiagnostics.PreviousStartupFailed
                ? null
                : _registryState.ActiveProject;
            _synchronizingProjectPicker = false;
            ProjectRegistryCountText.Text = $"{_registryState.Projects.Count} 个";
            AppDiagnostics.Log("Project picker synchronized");

            if (AppDiagnostics.PreviousStartupFailed)
            {
                ShowNoProjectState(preserveRegisteredProjects: true);
                SetStatus($"安全启动：已保留 {_registryState.Projects.Count} 个项目，请从顶部列表选择一个");
                AppDiagnostics.Log("Automatic project restore skipped by safe startup");
                return;
            }

            if (_registryState.ActiveProject is RegisteredProject project)
            {
                AppDiagnostics.Log($"Activating persisted project · {project.RootPath}");
                ActivateProject(project);
                AppDiagnostics.Log("Persisted project activation returned");
                if (recoveredRegistry)
                {
                    SetStatus($"项目列表已从备份恢复 · 共 {_registryState.Projects.Count} 个项目");
                }
            }
            else
            {
                ShowNoProjectState();
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException
                                           or NotSupportedException)
        {
            AppDiagnostics.Log("Project registry could not be loaded", exception);
            ProjectRegistryCountText.Text = "读取失败";
            SetStatus($"无法读取项目列表：{exception.Message}");
            ShowNoProjectState();
        }
    }

    private async void OnAddProjectClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            _registryState = await _registryStore.AddAsync(folder.Path);
            await ReloadRegistryAsync();
            SetStatus($"已添加项目：{folder.Path}");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or InvalidDataException)
        {
            SetStatus($"无法添加项目：{exception.Message}");
        }
    }

    private async void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _synchronizingProjectPicker || ProjectPicker.SelectedItem is not RegisteredProject project)
        {
            return;
        }

        if (_activeProject?.Id == project.Id)
        {
            return;
        }

        try
        {
            _registryState = await _registryStore.SetActiveAsync(project.Id);
            ActivateProject(project);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or KeyNotFoundException
                                           or InvalidDataException)
        {
            SetStatus($"无法切换项目：{exception.Message}");
        }
    }

    private async void OnManageProjectsClicked(object sender, RoutedEventArgs e)
    {
        if (_registryState.Projects.Count == 0)
        {
            SetStatus("尚未添加项目");
            return;
        }

        var projectList = new ListView
        {
            ItemsSource = _registryState.Projects,
            DisplayMemberPath = nameof(RegisteredProject.Name),
            SelectionMode = ListViewSelectionMode.Single,
            SelectedItem = _activeProject ?? _registryState.ActiveProject,
            MinHeight = 180,
            MaxHeight = 320
        };
        var projectPath = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["HubTextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var projectState = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["HubTextSecondaryBrush"],
            Margin = new Thickness(0, 4, 0, 0)
        };

        void UpdateProjectDetails()
        {
            if (projectList.SelectedItem is not RegisteredProject selected)
            {
                projectPath.Text = "请选择一个项目";
                projectState.Text = string.Empty;
                return;
            }

            projectPath.Text = selected.RootPath;
            projectState.Text = Directory.Exists(selected.RootPath)
                ? (selected.Id == _activeProject?.Id ? "当前项目" : "可切换")
                : "目录已不存在；可以将其移出管理清单";
        }

        projectList.SelectionChanged += (_, _) => UpdateProjectDetails();
        UpdateProjectDetails();

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = "File Hub 同一时间只打开一个项目。移出管理不会删除磁盘上的任何文件。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["HubTextSecondaryBrush"]
        });
        content.Children.Add(projectList);
        content.Children.Add(projectPath);
        content.Children.Add(projectState);

        var dialog = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "管理项目",
            Content = content,
            PrimaryButtonText = "切换到此项目",
            SecondaryButtonText = "移出管理",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (projectList.SelectedItem is not RegisteredProject project)
        {
            return;
        }

        try
        {
            if (result == ContentDialogResult.Primary)
            {
                if (!Directory.Exists(project.RootPath))
                {
                    SetStatus($"项目目录不存在：{project.RootPath}");
                    return;
                }

                _registryState = await _registryStore.SetActiveAsync(project.Id);
                await ReloadRegistryAsync();
                SetStatus($"已切换项目：{project.Name}");
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ConfirmRemoveProjectAsync(project);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or KeyNotFoundException
                                           or InvalidDataException)
        {
            SetStatus($"无法更新项目清单：{exception.Message}");
        }
    }

    private async Task ConfirmRemoveProjectAsync(RegisteredProject project)
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = $"移出“{project.Name}”？",
            Content = $"只会从 Project File Hub 的项目清单中移除。\n不会删除此目录或其中的文件：\n{project.RootPath}",
            PrimaryButtonText = "移出管理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _registryState = await _registryStore.RemoveAsync(project.Id);
        await ReloadRegistryAsync();
        SetStatus($"已将项目移出管理：{project.Name}；磁盘文件未删除");
    }

    private void ActivateProject(RegisteredProject project)
    {
        AppDiagnostics.Log($"ActivateProject entered · {project.RootPath}");
        if (!Directory.Exists(project.RootPath))
        {
            SetStatus($"项目目录不存在：{project.RootPath}");
            return;
        }

        try
        {
            var boundary = new PathBoundary(project.RootPath);
            boundary.EnsureSafe(project.RootPath);
            ClearUndo();
            SetMultiSelectMode(false, clearSelectionWhenDisabled: true, announce: false);

            _activeProject = project;
            var workspace = _settingsState.RestoreWorkspace
                ? _settingsState.GetWorkspace(project.Id)
                : null;
            ApplyWorkspaceControls(workspace);
            var initialFolder = ResolveWorkspaceFolder(project, workspace);
            _currentFolder = initialFolder;
            ProjectTree.RootNodes.Clear();
            AppDiagnostics.Log("Tree root nodes cleared");

            var rootNode = CreateDirectoryNode(project.RootPath);
            AppDiagnostics.Log("Tree root node created");
            ProjectTree.RootNodes.Add(rootNode);
            AppDiagnostics.Log("Tree root node added");
            LoadNodeChildren(rootNode);
            AppDiagnostics.Log($"Tree root children loaded · count={rootNode.Children.Count}");
            rootNode.IsExpanded = true;
            AppDiagnostics.Log("Tree root expanded");
            SelectAndExpandTreePath(rootNode, initialFolder);
            AppDiagnostics.Log($"Tree workspace selected · {initialFolder}");

            NavigateTo(initialFolder, persistWorkspace: false);
            AppDiagnostics.Log("Initial project navigation returned");
            StartProjectIndex(project);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"项目根目录不安全或不可访问：{exception.Message}");
        }
    }

    private string ResolveWorkspaceFolder(
        RegisteredProject project,
        ProjectWorkspaceState? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace?.RelativeFolder)
            || workspace.RelativeFolder == ".")
        {
            return project.RootPath;
        }

        try
        {
            var boundary = new PathBoundary(project.RootPath);
            var candidate = boundary.EnsureSafe(Path.Combine(project.RootPath, workspace.RelativeFolder));
            return Directory.Exists(candidate) ? candidate : project.RootPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return project.RootPath;
        }
    }

    private void ApplyWorkspaceControls(ProjectWorkspaceState? workspace)
    {
        _synchronizingWorkspaceControls = true;
        try
        {
            _sortField = workspace?.SortField ?? FileSortField.Name;
            _sortDirection = workspace?.SortDirection ?? SortDirection.Ascending;
            _categoryFilter = _settingsState.FilterRailVisible
                ? workspace?.CategoryFilter
                : null;

            SortPicker.SelectedItem = SortPicker.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    _sortField.ToString(),
                    StringComparison.Ordinal));
            SortDirectionIcon.Glyph = _sortDirection == SortDirection.Ascending ? "\uE74A" : "\uE74B";
            SetSelectedFilterControl(_categoryFilter);

            var useGrid = workspace?.GridView ?? true;
            FileGrid.Visibility = useGrid ? Visibility.Visible : Visibility.Collapsed;
            FileList.Visibility = useGrid ? Visibility.Collapsed : Visibility.Visible;
            UpdateActiveFilterState();
        }
        finally
        {
            _synchronizingWorkspaceControls = false;
        }
    }

    private void SelectAndExpandTreePath(TreeViewNode rootNode, string targetPath)
    {
        if (_activeProject is null)
        {
            return;
        }

        var selectedNode = rootNode;
        var relative = Path.GetRelativePath(_activeProject.RootPath, targetPath);
        if (relative != ".")
        {
            var currentPath = _activeProject.RootPath;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (selectedNode.HasUnrealizedChildren)
                {
                    LoadNodeChildren(selectedNode);
                }

                selectedNode.IsExpanded = true;
                currentPath = Path.Combine(currentPath, segment);
                var nextNode = selectedNode.Children.FirstOrDefault(node =>
                    node.Content is DirectoryNodeViewModel directory
                    && string.Equals(directory.FullPath, currentPath, StringComparison.OrdinalIgnoreCase));
                if (nextNode is null)
                {
                    break;
                }

                selectedNode = nextNode;
            }
        }

        ProjectTree.SelectedNode = selectedNode;
    }

    private TreeViewNode CreateDirectoryNode(string path)
    {
        var name = string.Equals(path, _activeProject?.RootPath, StringComparison.OrdinalIgnoreCase)
            ? _activeProject?.Name ?? new DirectoryInfo(path).Name
            : new DirectoryInfo(path).Name;

        return new TreeViewNode
        {
            Content = new DirectoryNodeViewModel(name, path),
            HasUnrealizedChildren = HasChildDirectories(path)
        };
    }

    private bool HasChildDirectories(string path)
    {
        if (_activeProject is null)
        {
            return false;
        }

        try
        {
            return _fileBrowser.GetChildDirectories(_activeProject.RootPath, path).Count > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void LoadNodeChildren(TreeViewNode node)
    {
        if (_activeProject is null || node.Content is not DirectoryNodeViewModel directory)
        {
            return;
        }

        try
        {
            node.Children.Clear();
            foreach (var childPath in _fileBrowser.GetChildDirectories(_activeProject.RootPath, directory.FullPath))
            {
                node.Children.Add(CreateDirectoryNode(childPath));
            }

            node.HasUnrealizedChildren = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"无法展开文件夹：{exception.Message}");
        }
    }

    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.HasUnrealizedChildren)
        {
            LoadNodeChildren(args.Node);
        }
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var directory = args.InvokedItem switch
        {
            DirectoryNodeViewModel direct => direct,
            TreeViewNode { Content: DirectoryNodeViewModel nested } => nested,
            _ => null
        };

        if (directory is not null)
        {
            NavigateTo(directory.FullPath);
        }
    }

    private void OnTreeFolderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TreeViewNode node })
        {
            return;
        }

        if (!node.IsExpanded && node.HasUnrealizedChildren)
        {
            LoadNodeChildren(node);
        }

        node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
    }

    private void RebuildProjectTree()
    {
        if (_activeProject is null || !Directory.Exists(_activeProject.RootPath))
        {
            return;
        }

        ProjectTree.RootNodes.Clear();
        var rootNode = CreateDirectoryNode(_activeProject.RootPath);
        ProjectTree.RootNodes.Add(rootNode);
        LoadNodeChildren(rootNode);
        rootNode.IsExpanded = true;
    }

    private void OnTreeMenuOpenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string path })
        {
            NavigateTo(path);
        }
    }

    private void OnTreeMenuOpenInExplorerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string path })
        {
            OpenInFileExplorer(path);
        }
    }

    private void OnTreeMenuRenameClicked(object sender, RoutedEventArgs e)
    {
        if (_activeProject is null || sender is not MenuFlyoutItem { Tag: string path })
        {
            return;
        }

        if (string.Equals(path, _activeProject.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("项目根目录不能在 File Hub 中重命名");
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return;
        }

        NavigateTo(parent);
        if (FindItem(path) is { } item)
        {
            BeginRename(item);
        }
    }

    private void OnTreeMenuNewFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string path })
        {
            CreateFolderAt(path);
        }
    }

    private void OnTreeMenuCopyPathClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string path })
        {
            CopyPaths([path]);
        }
    }

    private async void OnTreeMenuRecycleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string path })
        {
            await ConfirmRecycleAsync([path]);
        }
    }

    private void OnBackgroundMenuNewFolderClicked(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is not null)
        {
            CreateFolderAt(_currentFolder);
        }
    }

    private void OnBackgroundMenuRefreshClicked(object sender, RoutedEventArgs e)
    {
        RefreshFileView();
        RebuildProjectTree();
        if (_categoryFilter is null)
        {
            SetStatus("内容已刷新");
        }
    }

    private void OnBackgroundMenuOpenInExplorerClicked(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is not null)
        {
            OpenInFileExplorer(_currentFolder);
        }
    }

    private void OnBackgroundMenuCopyPathClicked(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is not null)
        {
            CopyPaths([_currentFolder]);
        }
    }

    private void CreateFolderAt(string parentPath)
    {
        if (_activeProject is null)
        {
            return;
        }

        try
        {
            string? createdPath = null;
            for (var suffix = 1; suffix <= 100; suffix++)
            {
                var name = suffix == 1 ? "新建文件夹" : $"新建文件夹 ({suffix})";
                var candidate = Path.Combine(parentPath, name);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    continue;
                }

                createdPath = _fileOperations.CreateDirectory(_activeProject.RootPath, parentPath, name);
                break;
            }

            if (createdPath is null)
            {
                throw new IOException("无法生成可用的新文件夹名称。");
            }

            RebuildProjectTree();
            if (string.Equals(_currentFolder, parentPath, StringComparison.OrdinalIgnoreCase))
            {
                RefreshFileView();
                if (FindItem(createdPath) is { } item)
                {
                    SelectPath(createdPath);
                    BeginRename(item);
                }
            }
            else
            {
                SetStatus($"已在 {Path.GetFileName(parentPath)} 中创建文件夹");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            SetStatus($"无法新建文件夹：{exception.Message}");
        }
    }

    private void OnFileDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        _draggedPaths = args.Items
            .OfType<ExplorerItemViewModel>()
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_draggedPaths.Count == 0)
        {
            args.Cancel = true;
            return;
        }

        args.Data.SetText(string.Join(Environment.NewLine, _draggedPaths));
        args.Data.Properties.Title = _draggedPaths.Count == 1
            ? Path.GetFileName(_draggedPaths[0])
            : $"{_draggedPaths.Count} 个项目";
        args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
    }

    private void OnFileDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _draggedPaths = [];
        CancelTreeHover();
    }

    private void OnContentFolderDragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExplorerItemViewModel { IsDirectory: true } item } element)
        {
            ConfigureDropTarget(element, e, item.FullPath, isTreeTarget: false);
        }
    }

    private void OnFileSurfaceDragOver(object sender, DragEventArgs e)
    {
        if (_currentFolder is not null && sender is FrameworkElement element)
        {
            ConfigureDropTarget(element, e, _currentFolder, isTreeTarget: false);
        }
    }

    private void OnTreeFolderDragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TreeViewNode { Content: DirectoryNodeViewModel directory } } element)
        {
            ConfigureDropTarget(element, e, directory.FullPath, isTreeTarget: true);
        }
    }

    private void ConfigureDropTarget(FrameworkElement element, DragEventArgs e, string destination, bool isTreeTarget)
    {
        e.AcceptedOperation = DataPackageOperation.None;
        ResetDropTargetVisual(element);

        if (_activeProject is null)
        {
            return;
        }

        if (_draggedPaths.Count == 0)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = $"复制到 {GetFolderLabel(destination)}";
                e.DragUIOverride.IsCaptionVisible = true;
                SetDropTargetVisual(element);
                if (isTreeTarget)
                {
                    QueueTreeAutoExpand(destination);
                }

                e.Handled = true;
            }

            return;
        }

        var mode = GetTransferMode(e);
        try
        {
            _fileOperations.PlanTransfer(_activeProject.RootPath, _draggedPaths, destination, mode);
            e.AcceptedOperation = mode == FileTransferMode.Copy
                ? DataPackageOperation.Copy
                : DataPackageOperation.Move;
            e.DragUIOverride.Caption = mode == FileTransferMode.Copy
                ? $"复制到 {Path.GetFileName(destination)}"
                : $"移动到 {Path.GetFileName(destination)}";
            e.DragUIOverride.IsCaptionVisible = true;
            SetDropTargetVisual(element);

            if (isTreeTarget)
            {
                QueueTreeAutoExpand(destination);
            }

            e.Handled = true;
        }
        catch (FileConflictException)
        {
            e.AcceptedOperation = mode == FileTransferMode.Copy
                ? DataPackageOperation.Copy
                : DataPackageOperation.Move;
            e.DragUIOverride.Caption = $"{GetFolderLabel(destination)} 中存在同名项目，放下后选择处理方式";
            e.DragUIOverride.IsCaptionVisible = true;
            SetDropTargetVisual(element);
            if (isTreeTarget)
            {
                QueueTreeAutoExpand(destination);
            }

            e.Handled = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            e.DragUIOverride.Caption = exception.Message;
            e.DragUIOverride.IsCaptionVisible = true;
            if (isTreeTarget)
            {
                CancelTreeHover();
            }
        }
    }

    private static string GetFolderLabel(string destination)
    {
        var label = Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(label) ? destination : label;
    }

    private void OnFolderDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            ResetDropTargetVisual(element);
        }

        if (sender is FrameworkElement { DataContext: TreeViewNode { Content: DirectoryNodeViewModel } })
        {
            CancelTreeHover();
        }
    }

    private void OnContentFolderDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExplorerItemViewModel { IsDirectory: true } item } element)
        {
            CompleteDrop(element, e, item.FullPath);
        }
    }

    private void OnFileSurfaceDrop(object sender, DragEventArgs e)
    {
        if (_currentFolder is not null && sender is FrameworkElement element)
        {
            CompleteDrop(element, e, _currentFolder);
        }
    }

    private void OnTreeFolderDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TreeViewNode { Content: DirectoryNodeViewModel directory } } element)
        {
            CompleteDrop(element, e, directory.FullPath);
        }
    }

    private async void CompleteDrop(FrameworkElement element, DragEventArgs e, string destination)
    {
        ResetDropTargetVisual(element);
        CancelTreeHover();
        e.Handled = true;

        if (_activeProject is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<FileOperationResult>? results;
            string action;
            DataPackageOperation acceptedOperation;

            if (_draggedPaths.Count > 0)
            {
                var mode = GetTransferMode(e);
                results = await ExecuteInternalTransferAsync(_draggedPaths, destination, mode);
                action = mode == FileTransferMode.Copy ? "复制" : "移动";
                acceptedOperation = mode == FileTransferMode.Copy
                    ? DataPackageOperation.Copy
                    : DataPackageOperation.Move;
            }
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                var externalPaths = storageItems
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                results = await ExecuteExternalImportAsync(externalPaths, destination);
                action = "复制";
                acceptedOperation = DataPackageOperation.Copy;
            }
            else
            {
                return;
            }

            if (results is null)
            {
                SetStatus("操作已取消");
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            if (results.Count > 0)
            {
                if (results.Any(result => result.ReplacedExisting))
                {
                    ClearUndo();
                }
                else
                {
                    var projectRoot = _activeProject.RootPath;
                    var undoResults = results.ToArray();
                    RegisterUndo(action, () => UndoTransferAsync(projectRoot, undoResults));
                }
            }

            RefreshFileView();
            RebuildProjectTree();
            SetStatus(results.Count == 0
                ? "冲突项目已跳过，没有文件发生变化"
                : $"已{action} {results.Count} 个项目到 {GetFolderLabel(destination)}");
            e.AcceptedOperation = acceptedOperation;
        }
        catch (Exception exception)
        {
            AppDiagnostics.Log("Drop operation failed", exception);
            SetStatus($"拖放未完成：{exception.Message}");
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async Task<IReadOnlyList<FileOperationResult>?> ExecuteInternalTransferAsync(
        IReadOnlyList<string> paths,
        string destination,
        FileTransferMode mode)
    {
        if (_activeProject is null)
        {
            return [];
        }

        try
        {
            return _fileOperations.Transfer(_activeProject.RootPath, paths, destination, mode);
        }
        catch (FileConflictException exception)
        {
            var resolution = await ShowConflictResolutionDialogAsync(exception.Conflicts);
            return resolution is null
                ? null
                : _fileOperations.Transfer(_activeProject.RootPath, paths, destination, mode, resolution.Value);
        }
    }

    private async Task<IReadOnlyList<FileOperationResult>?> ExecuteExternalImportAsync(
        IReadOnlyList<string> paths,
        string destination)
    {
        if (_activeProject is null)
        {
            return [];
        }

        try
        {
            return _fileOperations.ImportCopy(_activeProject.RootPath, paths, destination);
        }
        catch (FileConflictException exception)
        {
            var resolution = await ShowConflictResolutionDialogAsync(exception.Conflicts);
            return resolution is null
                ? null
                : _fileOperations.ImportCopy(_activeProject.RootPath, paths, destination, resolution.Value);
        }
    }

    private async Task<FileConflictResolution?> ShowConflictResolutionDialogAsync(
        IReadOnlyList<FileOperationConflict> conflicts)
    {
        var keepBoth = new RadioButton
        {
            Content = "保留两者 — 为新项目自动添加编号",
            GroupName = "ConflictResolution",
            IsChecked = true
        };
        var replace = new RadioButton
        {
            Content = conflicts.Any(conflict => conflict.IsDirectory)
                ? "替换现有文件 — 当前选择包含文件夹，暂不可用"
                : "替换现有文件 — 原文件会被覆盖",
            GroupName = "ConflictResolution",
            IsEnabled = conflicts.All(conflict => !conflict.IsDirectory)
        };
        var skip = new RadioButton
        {
            Content = "跳过冲突 — 只处理没有同名项的内容",
            GroupName = "ConflictResolution"
        };

        var details = string.Join(
            Environment.NewLine,
            conflicts.Take(4).Select(conflict => $"• {Path.GetFileName(conflict.DestinationPath)}"));
        if (conflicts.Count > 4)
        {
            details += $"{Environment.NewLine}• 以及另外 {conflicts.Count - 4} 个项目";
        }

        var content = new StackPanel { Spacing = 10, MaxWidth = 480 };
        content.Children.Add(new TextBlock
        {
            Text = $"目标文件夹中存在 {conflicts.Count} 个同名项目：{Environment.NewLine}{details}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["HubTextSecondaryBrush"]
        });
        content.Children.Add(keepBoth);
        content.Children.Add(replace);
        content.Children.Add(skip);

        var dialog = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "处理同名项目",
            Content = content,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        if (replace.IsChecked == true)
        {
            return FileConflictResolution.Replace;
        }

        return skip.IsChecked == true
            ? FileConflictResolution.Skip
            : FileConflictResolution.KeepBoth;
    }

    private static FileTransferMode GetTransferMode(DragEventArgs e) =>
        e.Modifiers.HasFlag(DragDropModifiers.Control)
            ? FileTransferMode.Copy
            : FileTransferMode.Move;

    private static void SetDropTargetVisual(FrameworkElement element)
    {
        if (element is Border border)
        {
            border.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 25, 181, 254));
            border.BorderThickness = new Thickness(2);
        }
        else if (element is Grid grid)
        {
            grid.Background = new SolidColorBrush(ColorHelper.FromArgb(70, 14, 165, 233));
        }
    }

    private static void ResetDropTargetVisual(FrameworkElement element)
    {
        if (element is Border border)
        {
            border.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 32, 49, 68));
            border.BorderThickness = new Thickness(1);
        }
        else if (element is Grid grid)
        {
            grid.Background = new SolidColorBrush(Colors.Transparent);
        }
    }

    private void QueueTreeAutoExpand(string path)
    {
        if (string.Equals(_hoveredTreePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _hoveredTreePath = path;
        _treeHoverTimer.Stop();
        _treeHoverTimer.Start();
    }

    private void CancelTreeHover()
    {
        _treeHoverTimer.Stop();
        _hoveredTreePath = null;
    }

    private void OnTreeHoverTimerTick(object? sender, object e)
    {
        _treeHoverTimer.Stop();
        if (_hoveredTreePath is null)
        {
            return;
        }

        var node = FindTreeNode(ProjectTree.RootNodes, _hoveredTreePath);
        if (node is not null)
        {
            if (node.HasUnrealizedChildren)
            {
                LoadNodeChildren(node);
            }

            node.IsExpanded = true;
        }
    }

    private static TreeViewNode? FindTreeNode(IEnumerable<TreeViewNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (node.Content is DirectoryNodeViewModel directory
                && string.Equals(directory.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var match = FindTreeNode(node.Children, path);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void NavigateTo(string folderPath, bool persistWorkspace = true)
    {
        if (_activeProject is null)
        {
            return;
        }

        try
        {
            var boundary = new PathBoundary(_activeProject.RootPath);
            CancelFileViewRefresh();
            _currentFolder = boundary.EnsureSafe(folderPath);
            _selectedItem = null;
            Items.Clear();
            PreviewItems.Clear();
            UpdateInspector(null);
            SelectionStatusText.Text = "未选择文件";
            UpdateMultiSelectionUi();
            UpdateFolderHeader();
            RefreshFileView();
            if (persistWorkspace)
            {
                ScheduleWorkspaceSave();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"已阻止导航：{exception.Message}");
        }
    }

    private void UpdateFolderHeader()
    {
        if (_activeProject is null || _currentFolder is null)
        {
            return;
        }

        var relative = Path.GetRelativePath(_activeProject.RootPath, _currentFolder);
        var isRoot = relative == ".";
        var location = isRoot ? _activeProject.RootPath : relative;
        var breadcrumb = isRoot
            ? $"{_activeProject.Name}  /"
            : $"{_activeProject.Name}  /  {relative.Replace(Path.DirectorySeparatorChar, '／')}";

        FolderTitle.Text = isRoot ? _activeProject.Name : new DirectoryInfo(_currentFolder).Name;
        if (_categoryFilter is FileItemCategory category)
        {
            var categoryName = GetCategoryName(category);
            FolderSubtitle.Text = $"{location}  ·  当前文件夹仅显示{categoryName}";
            BreadcrumbText.Text = $"{breadcrumb}  /  {categoryName}";
        }
        else
        {
            FolderSubtitle.Text = location;
            BreadcrumbText.Text = breadcrumb;
        }
    }

    private void RefreshFileView()
    {
        if (_activeProject is null || _currentFolder is null)
        {
            ShowNoProjectState();
            return;
        }

        if (_categoryFilter is not null)
        {
            _ = RefreshCurrentFolderCategoryAsync();
            return;
        }

        CancelFileViewRefresh();

        try
        {
            var results = _fileBrowser.GetItems(
                _activeProject.RootPath,
                _currentFolder,
                new FileQueryOptions(_sortField, _sortDirection, _categoryFilter));

            Items.Clear();
            foreach (var item in results)
            {
                Items.Add(new ExplorerItemViewModel(item));
            }

            _selectedItem = null;
            PreviewItems.Clear();
            ItemCountText.Text = $"{Items.Count} 个项目";
            EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateTitle.Text = Items.Count == 0 ? "这个位置没有匹配的文件" : string.Empty;
            EmptyStateMessage.Text = _categoryFilter is null ? "文件夹为空" : "可以切换到其他文件类型";
            UpdateInspector(null);
            SelectionStatusText.Text = "未选择文件";
            UpdateMultiSelectionUi();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Items.Clear();
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateTitle.Text = "无法读取此文件夹";
            EmptyStateMessage.Text = exception.Message;
            SetStatus(exception.Message);
        }
    }

    private void ShowNoProjectState(bool preserveRegisteredProjects = false)
    {
        CancelFileViewRefresh();
        StopProjectIndex();
        SetMultiSelectMode(false, clearSelectionWhenDisabled: true, announce: false);
        _activeProject = null;
        ClearUndo();
        _currentFolder = null;
        ProjectTree.RootNodes.Clear();
        Items.Clear();
        PreviewItems.Clear();
        var registeredProjectCount = _registryState.Projects.Count;
        var hasPreservedProjects = preserveRegisteredProjects && registeredProjectCount > 0;
        BreadcrumbText.Text = hasPreservedProjects ? "项目列表已保留" : "尚未打开项目";
        FolderTitle.Text = hasPreservedProjects ? "选择一个项目" : "欢迎";
        FolderSubtitle.Text = hasPreservedProjects
            ? $"上次启动未完成；{registeredProjectCount} 个已登记项目仍然保留"
            : "添加一个本机项目开始管理";
        ItemCountText.Text = hasPreservedProjects ? $"{registeredProjectCount} 个已登记项目" : "0 个项目";
        EmptyState.Visibility = Visibility.Visible;
        EmptyStateTitle.Text = hasPreservedProjects ? "项目列表没有丢失" : "尚未添加项目";
        EmptyStateMessage.Text = hasPreservedProjects
            ? "请从顶部项目下拉框选择一个项目继续"
            : "只管理你明确添加的项目目录";
        UpdateInspector(null);
        SelectionStatusText.Text = "未选择文件";
        UpdateMultiSelectionUi();
    }

    private void OnTypeFilterClicked(object sender, RoutedEventArgs e)
    {
        if (!_loaded
            || _synchronizingWorkspaceControls
            || sender is not Button button
            || button.Tag is not string value)
        {
            return;
        }

        FileItemCategory? requestedCategory = string.Equals(value, "All", StringComparison.Ordinal)
            ? null
            : Enum.Parse<FileItemCategory>(value);
        if (requestedCategory is FileItemCategory category && _categoryFilter == category)
        {
            requestedCategory = null;
        }

        ApplyCategoryFilter(requestedCategory);
    }

    private void OnClearTypeFilterClicked(object sender, RoutedEventArgs e) =>
        ApplyCategoryFilter(null);

    private void ApplyCategoryFilter(FileItemCategory? category)
    {
        _categoryFilter = category;
        SetSelectedFilterControl(category);
        UpdateActiveFilterState();
        UpdateFolderHeader();
        RefreshFileView();
        ScheduleWorkspaceSave();
    }

    private void SetSelectedFilterControl(FileItemCategory? category)
    {
        SetFilterVisualState(category is null, AllFilterSelection, AllFilterIcon, AllFilterLabel);
        SetFilterVisualState(category == FileItemCategory.Image, ImageFilterSelection, ImageFilterIcon, ImageFilterLabel);
        SetFilterVisualState(category == FileItemCategory.Video, VideoFilterSelection, VideoFilterIcon, VideoFilterLabel);
        SetFilterVisualState(category == FileItemCategory.Audio, AudioFilterSelection, AudioFilterIcon, AudioFilterLabel);
        SetFilterVisualState(category == FileItemCategory.Document, DocumentFilterSelection, DocumentFilterIcon, DocumentFilterLabel);
        SetFilterVisualState(category == FileItemCategory.Code, CodeFilterSelection, CodeFilterIcon, CodeFilterLabel);
    }

    private static void SetFilterVisualState(
        bool selected,
        Border selection,
        FontIcon icon,
        TextBlock label)
    {
        selection.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        var brush = (Brush)Application.Current.Resources[
            selected ? "HubAccentStrongBrush" : "HubTextSecondaryBrush"];
        icon.Foreground = brush;
        label.Foreground = brush;
    }

    private void UpdateActiveFilterState()
    {
        if (_categoryFilter is not FileItemCategory category)
        {
            ActiveFilterButton.Visibility = Visibility.Collapsed;
            return;
        }

        ActiveFilterText.Text = $"{GetCategoryName(category)} · 当前文件夹";
        ActiveFilterButton.Visibility = Visibility.Visible;
    }

    private void StartProjectIndex(RegisteredProject project)
    {
        StopProjectIndex();

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var databasePath = Path.Combine(localData, "ProjectFileHub", "indexes", $"{project.Id:N}.db");
        var service = new ProjectIndexService(project.RootPath, databasePath);
        var cancellation = new CancellationTokenSource();
        service.IndexChanged += OnProjectIndexChanged;
        service.IndexingFailed += OnProjectIndexingFailed;

        _projectIndex = service;
        _indexCancellation = cancellation;
        _indexInitialization = InitializeProjectIndexAsync(service, project, cancellation.Token);
        SetStatus("正在后台整理整个项目的文件分类…");
    }

    private async Task InitializeProjectIndexAsync(
        ProjectIndexService service,
        RegisteredProject project,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => service.InitializeAsync(cancellationToken), cancellationToken);
            if (ReferenceEquals(service, _projectIndex)
                && _activeProject?.Id == project.Id
                && _categoryFilter is null)
            {
                SetStatus($"项目分类已就绪 · 已整理 {service.IndexedItemCount} 个项目");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when switching projects or closing the window.
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(service, _projectIndex))
            {
                AppDiagnostics.Log($"Project index initialization failed · {exception}");
                SetStatus($"项目分类暂时不可用：{exception.Message}");
            }
        }
    }

    private async Task RefreshCurrentFolderCategoryAsync()
    {
        if (_activeProject is not RegisteredProject project
            || _categoryFilter is not FileItemCategory category
            || _currentFolder is not string folderPath)
        {
            return;
        }

        var cancellation = StartFileViewRefresh(out var requestVersion);
        var cancellationToken = cancellation.Token;
        var categoryName = GetCategoryName(category);
        var folderName = string.Equals(folderPath, project.RootPath, StringComparison.OrdinalIgnoreCase)
            ? project.Name
            : new DirectoryInfo(folderPath).Name;
        var loadingStarted = DateTimeOffset.UtcNow;
        ShowFileLoading(folderName, categoryName);
        await Task.Yield();

        try
        {
            var progress = new Progress<int>(scannedCount =>
            {
                if (requestVersion == _fileViewVersion)
                {
                    FileLoadingDetail.Text = $"已检查 {scannedCount} 个项目，正在匹配{categoryName}";
                }
            });

            var results = await Task.Run(
                () => _fileBrowser.GetItems(
                    project.RootPath,
                    folderPath,
                    new FileQueryOptions(_sortField, _sortDirection, category),
                    progress,
                    cancellationToken),
                cancellationToken);
            EnsureCurrentFileViewRequest(requestVersion, project.Id, folderPath, category, cancellationToken);

            Items.Clear();
            for (var index = 0; index < results.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Items.Add(new ExplorerItemViewModel(results[index]));
                if ((index + 1) % 80 == 0)
                {
                    FileLoadingDetail.Text = $"正在显示 {index + 1} / {results.Count} 个{categoryName}";
                    await Task.Yield();
                    EnsureCurrentFileViewRequest(requestVersion, project.Id, folderPath, category, cancellationToken);
                }
            }

            var minimumVisibleTime = TimeSpan.FromMilliseconds(180);
            var elapsed = DateTimeOffset.UtcNow - loadingStarted;
            if (elapsed < minimumVisibleTime)
            {
                await Task.Delay(minimumVisibleTime - elapsed, cancellationToken);
            }

            EnsureCurrentFileViewRequest(requestVersion, project.Id, folderPath, category, cancellationToken);

            _selectedItem = null;
            PreviewItems.Clear();
            ItemCountText.Text = $"{Items.Count} 个{categoryName}";
            EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateTitle.Text = Items.Count == 0 ? $"当前文件夹中没有{categoryName}" : string.Empty;
            EmptyStateMessage.Text = Items.Count == 0 ? "可以切换左侧文件夹或选择其他文件类型" : string.Empty;
            UpdateInspector(null);
            SelectionStatusText.Text = "未选择文件";
            UpdateMultiSelectionUi();
            SetStatus($"{folderName} · 已显示 {Items.Count} 个{categoryName}");
        }
        catch (OperationCanceledException)
        {
            // A folder, filter, sort, or project change superseded this request.
        }
        catch (Exception exception)
        {
            if (requestVersion == _fileViewVersion)
            {
                EmptyState.Visibility = Visibility.Visible;
                EmptyStateTitle.Text = "暂时无法筛选当前文件夹";
                EmptyStateMessage.Text = exception.Message;
                SetStatus($"当前文件夹筛选失败：{exception.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_fileViewCancellation, cancellation))
            {
                _fileViewCancellation.Dispose();
                _fileViewCancellation = null;
                HideFileLoading();
            }
        }
    }

    private CancellationTokenSource StartFileViewRefresh(out int requestVersion)
    {
        _fileViewCancellation?.Cancel();
        _fileViewCancellation?.Dispose();
        _fileViewCancellation = new CancellationTokenSource();
        requestVersion = ++_fileViewVersion;
        return _fileViewCancellation;
    }

    private void CancelFileViewRefresh()
    {
        _fileViewVersion++;
        _fileViewCancellation?.Cancel();
        _fileViewCancellation?.Dispose();
        _fileViewCancellation = null;
        HideFileLoading();
    }

    private void EnsureCurrentFileViewRequest(
        int requestVersion,
        Guid projectId,
        string folderPath,
        FileItemCategory category,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestVersion != _fileViewVersion
            || _activeProject?.Id != projectId
            || !string.Equals(_currentFolder, folderPath, StringComparison.OrdinalIgnoreCase)
            || _categoryFilter != category)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private void ShowFileLoading(string folderName, string categoryName)
    {
        FileLoadingTitle.Text = $"正在筛选“{folderName}”中的{categoryName}";
        FileLoadingDetail.Text = "正在读取当前文件夹…";
        FileLoadingRing.IsActive = true;
        FileLoadingOverlay.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        FileGrid.IsEnabled = false;
        FileList.IsEnabled = false;
        FileGrid.Opacity = 0.35;
        FileList.Opacity = 0.35;
        ItemCountText.Text = "正在加载…";
        SetStatus($"正在筛选当前文件夹中的{categoryName}…");
    }

    private void HideFileLoading()
    {
        FileLoadingRing.IsActive = false;
        FileLoadingOverlay.Visibility = Visibility.Collapsed;
        FileGrid.IsEnabled = true;
        FileList.IsEnabled = true;
        FileGrid.Opacity = 1;
        FileList.Opacity = 1;
    }

    private void OnProjectIndexChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _projectIndex))
        {
            return;
        }

        // The index remains available for future project-wide search, but the type rail now
        // follows the current tree folder and refreshes directly from that folder.
    }

    private void OnProjectIndexingFailed(object? sender, string message)
    {
        if (!ReferenceEquals(sender, _projectIndex))
        {
            return;
        }

        RootLayout.DispatcherQueue.TryEnqueue(() => SetStatus($"项目分类更新失败：{message}"));
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_applicationExitRequested || !IsNotificationAreaEnabled)
        {
            return;
        }

        args.Cancel = true;
        HideWindowToTray(showTip: true);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_loaded
            || _applicationExitRequested
            || _isHidingToTray
            || !IsNotificationAreaEnabled
            || !args.DidPresenterChange
            || sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            return;
        }

        HideWindowToTray(showTip: true);
    }

    private void HideWindowToTray(bool showTip)
    {
        if (_appWindow is null
            || !IsNotificationAreaEnabled
            || _isHidingToTray)
        {
            return;
        }

        _isHidingToTray = true;
        try
        {
            ClosePreview();
            SettingsOverlay.Visibility = Visibility.Collapsed;
            UpdateCurrentWorkspaceSnapshot();
            _ = SaveSettingsSnapshotAsync(_settingsState);
            _appWindow.Hide();
            AppDiagnostics.Log("Main window hidden to notification area");
            SetStatus("Project File Hub 正在后台运行");
            if (showTip)
            {
                _notificationAreaService?.ShowBackgroundTip();
            }
        }
        finally
        {
            _isHidingToTray = false;
        }
    }

    public void RestoreFromTray()
    {
        if (_appWindow is null || _applicationExitRequested)
        {
            return;
        }

        _isHidingToTray = true;
        try
        {
            if (_appWindow.Presenter is OverlappedPresenter
                {
                    State: OverlappedPresenterState.Minimized
                } presenter)
            {
                presenter.Restore();
            }

            _appWindow.Show(activateWindow: true);
            Activate();
            var windowHandle = WindowNative.GetWindowHandle(this);
            var broughtToFront = WindowActivationService.BringToForeground(windowHandle);
            AppDiagnostics.Log($"Main window restored from notification area · foreground={broughtToFront}");
            SetStatus("已从通知区域恢复");
        }
        finally
        {
            _isHidingToTray = false;
        }
    }

    private NotificationAreaSnapshot GetNotificationAreaSnapshot()
    {
        var projectName = _activeProject?.Name ?? "未选择项目";
        if (_projectIndex is not { } service)
        {
            return new NotificationAreaSnapshot(projectName, "索引：未启动", false, false);
        }

        if (service.IsPaused)
        {
            return new NotificationAreaSnapshot(
                projectName,
                $"索引：已暂停 · {service.IndexedItemCount} 项",
                true,
                true);
        }

        var initialized = _indexInitialization?.IsCompleted == true;
        return new NotificationAreaSnapshot(
            projectName,
            initialized
                ? $"索引：运行中 · {service.IndexedItemCount} 项"
                : "索引：正在整理…",
            initialized,
            false);
    }

    private void ToggleProjectIndexFromTray()
    {
        if (_projectIndex is not { } service || _indexInitialization?.IsCompleted != true)
        {
            return;
        }

        if (service.IsPaused)
        {
            service.Resume();
            SetStatus("后台索引已恢复，正在同步暂停期间的变化…");
        }
        else
        {
            service.Pause();
            SetStatus("后台索引已暂停");
        }
    }

    private void RequestApplicationExit()
    {
        _applicationExitRequested = true;
        Close();
    }

    private void StopProjectIndex()
    {
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _indexCancellation = null;
        _indexInitialization = null;

        if (_projectIndex is not { } service)
        {
            return;
        }

        service.IndexChanged -= OnProjectIndexChanged;
        service.IndexingFailed -= OnProjectIndexingFailed;
        _projectIndex = null;
        _ = DisposeProjectIndexAsync(service);
    }

    private static async Task DisposeProjectIndexAsync(ProjectIndexService service)
    {
        try
        {
            await service.DisposeAsync();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent window/project shutdown may already own disposal.
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _applicationExitRequested = true;
        UpdateCurrentWorkspaceSnapshot();
        try
        {
            _settingsStore.SaveAsync(_settingsState).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Log("Workspace settings could not be saved during shutdown", exception);
        }

        CancelFileViewRefresh();
        StopProjectIndex();
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
            _appWindow = null;
        }

        _notificationAreaService?.Dispose();
        _notificationAreaService = null;
        _minimumWindowSizeService?.Dispose();
        _minimumWindowSizeService = null;
    }

    private void ScheduleWorkspaceSave()
    {
        if (!UpdateCurrentWorkspaceSnapshot())
        {
            return;
        }

        var snapshot = _settingsState;
        _ = SaveSettingsSnapshotAsync(snapshot);
    }

    private bool UpdateCurrentWorkspaceSnapshot()
    {
        if (!_loaded
            || !_settingsState.RestoreWorkspace
            || _activeProject is null
            || _currentFolder is null)
        {
            return false;
        }

        try
        {
            var boundary = new PathBoundary(_activeProject.RootPath);
            var safeFolder = boundary.EnsureSafe(_currentFolder);
            var workspaces = new Dictionary<Guid, ProjectWorkspaceState>(_settingsState.ProjectWorkspaces)
            {
                [_activeProject.Id] = new ProjectWorkspaceState
                {
                    RelativeFolder = Path.GetRelativePath(_activeProject.RootPath, safeFolder),
                    CategoryFilter = _categoryFilter,
                    SortField = _sortField,
                    SortDirection = _sortDirection,
                    GridView = FileGrid.Visibility == Visibility.Visible
                }
            };
            _settingsState = _settingsState with { ProjectWorkspaces = workspaces };
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppDiagnostics.Log("Workspace snapshot was rejected", exception);
            return false;
        }
    }

    private async Task SaveSettingsSnapshotAsync(AppSettingsState snapshot)
    {
        try
        {
            await _settingsStore.SaveAsync(snapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppDiagnostics.Log("Workspace settings save failed", exception);
            RootLayout.DispatcherQueue.TryEnqueue(() => SetStatus($"工作区记忆未保存：{exception.Message}"));
        }
    }

    private static string GetCategoryName(FileItemCategory category) => category switch
    {
        FileItemCategory.Image => "图片",
        FileItemCategory.Video => "视频",
        FileItemCategory.Audio => "音频",
        FileItemCategory.Document => "文档",
        FileItemCategory.Code => "代码",
        FileItemCategory.Archive => "压缩包",
        _ => "文件"
    };

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded
            || _synchronizingWorkspaceControls
            || SortPicker.SelectedItem is not ComboBoxItem item
            || item.Tag is not string value)
        {
            return;
        }

        _sortField = Enum.Parse<FileSortField>(value);
        RefreshFileView();
        ScheduleWorkspaceSave();
    }

    private void OnSortDirectionClicked(object sender, RoutedEventArgs e)
    {
        _sortDirection = _sortDirection == SortDirection.Ascending
            ? SortDirection.Descending
            : SortDirection.Ascending;
        SortDirectionIcon.Glyph = _sortDirection == SortDirection.Ascending ? "\uE74A" : "\uE74B";
        RefreshFileView();
        ScheduleWorkspaceSave();
    }

    private void OnGridModeClicked(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        FileGrid.Visibility = Visibility.Visible;
        FileList.Visibility = Visibility.Collapsed;
        ApplySelectionToView(FileGrid, selected);
        ScheduleWorkspaceSave();
    }

    private void OnListModeClicked(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        FileGrid.Visibility = Visibility.Collapsed;
        FileList.Visibility = Visibility.Visible;
        ApplySelectionToView(FileList, selected);
        ScheduleWorkspaceSave();
    }

    private void OnToggleMultiSelectClicked(object sender, RoutedEventArgs e) =>
        SetMultiSelectMode(!_multiSelectMode, clearSelectionWhenDisabled: true);

    private void OnExitMultiSelectClicked(object sender, RoutedEventArgs e) =>
        SetMultiSelectMode(false, clearSelectionWhenDisabled: true);

    private void SetMultiSelectMode(
        bool enabled,
        bool clearSelectionWhenDisabled,
        bool announce = true)
    {
        var selected = enabled ? GetSelectedItems() : [];
        _synchronizingSelection = true;
        try
        {
            _multiSelectMode = enabled;
            FileGrid.SelectionMode = enabled
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Extended;
            FileList.SelectionMode = enabled
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Extended;
            FileGrid.IsMultiSelectCheckBoxEnabled = enabled;
            FileList.IsMultiSelectCheckBoxEnabled = enabled;

            if (enabled)
            {
                ApplySelectionToView(FileGrid, selected);
                ApplySelectionToView(FileList, selected);
            }
            else if (clearSelectionWhenDisabled)
            {
                FileGrid.SelectedItems.Clear();
                FileList.SelectedItems.Clear();
                _selectedItem = null;
                UpdateInspector(null);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }

        UpdateItemSelectionStates(GetSelectedItems());

        MultiSelectLabel.Text = enabled ? "完成" : "多选";
        MultiSelectButton.Background = (Brush)Application.Current.Resources[
            enabled ? "HubSelectedBrush" : "HubRaisedBrush"];
        MultiSelectButton.BorderBrush = (Brush)Application.Current.Resources[
            enabled ? "HubAccentBrush" : "HubBorderBrush"];
        UpdateMultiSelectionUi();
        if (announce)
        {
            SetStatus(enabled ? "多选模式已开启；点击文件即可连续选择" : "已退出多选模式");
        }
    }

    private void OnSelectAllClicked(object sender, RoutedEventArgs e) =>
        SelectAllItems();

    private void SelectAllItems()
    {
        if (!_multiSelectMode)
        {
            SetMultiSelectMode(true, clearSelectionWhenDisabled: false);
        }

        ActiveFileView.SelectAll();
        UpdateItemSelectionStates(GetSelectedItems());
        UpdateMultiSelectionUi();
    }

    private void OnClearSelectionClicked(object sender, RoutedEventArgs e)
    {
        _synchronizingSelection = true;
        try
        {
            FileGrid.SelectedItems.Clear();
            FileList.SelectedItems.Clear();
            _selectedItem = null;
        }
        finally
        {
            _synchronizingSelection = false;
        }

        UpdateItemSelectionStates([]);
        UpdateInspector(null);
        UpdateMultiSelectionUi();
        SetStatus("已清除选择");
    }

    private async void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection || sender is not ListViewBase source)
        {
            return;
        }

        _synchronizingSelection = true;
        var selected = source.SelectedItems.OfType<ExplorerItemViewModel>().ToArray();
        _selectedItem = source.SelectedItem as ExplorerItemViewModel ?? selected.LastOrDefault();
        ApplySelectionToView(ReferenceEquals(source, FileGrid) ? FileList : FileGrid, selected);
        UpdateItemSelectionStates(selected);
        _synchronizingSelection = false;
        await UpdateInspectorAsync(_selectedItem);
        UpdateMultiSelectionUi();
        if (selected.Length > 1)
        {
            SetStatus($"已选择 {selected.Length} 个项目");
        }
    }

    private ExplorerItemViewModel[] GetSelectedItems() =>
        ActiveFileView.SelectedItems.OfType<ExplorerItemViewModel>().ToArray();

    private void ApplySelectionToView(
        ListViewBase target,
        IReadOnlyCollection<ExplorerItemViewModel> selected)
    {
        target.SelectedItems.Clear();
        foreach (var item in selected)
        {
            if (Items.Contains(item))
            {
                target.SelectedItems.Add(item);
            }
        }

        target.SelectedItem = _selectedItem is not null && Items.Contains(_selectedItem)
            ? _selectedItem
            : selected.FirstOrDefault();
    }

    private void UpdateItemSelectionStates(IEnumerable<ExplorerItemViewModel> selected)
    {
        var selectedPaths = selected
            .Select(item => item.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Items)
        {
            item.IsSelected = selectedPaths.Contains(item.FullPath);
        }
    }

    private void UpdateMultiSelectionUi()
    {
        var count = GetSelectedItems().Length;
        MultiSelectionCountText.Text = $"已选择 {count} 项";
        SelectionActionBar.Visibility = _multiSelectMode || count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        CopySelectionButton.IsEnabled = count > 0;
        CopySelectionToButton.IsEnabled = count > 0;
        MoveSelectionToButton.IsEnabled = count > 0;
        RecycleSelectionButton.IsEnabled = count > 0;
    }

    private void OnFileContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue
            && args.Item is ExplorerItemViewModel { Item.IsImage: true } item)
        {
            _ = item.LoadThumbnailAsync();
        }
    }

    private ListViewBase ActiveFileView =>
        FileGrid.Visibility == Visibility.Visible ? FileGrid : FileList;

    private ExplorerItemViewModel? FindItem(string fullPath) =>
        Items.FirstOrDefault(item =>
            string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ExplorerItemViewModel item)
        {
            return;
        }

        var view = ActiveFileView;
        if (!view.SelectedItems.Contains(item))
        {
            view.SelectedItems.Clear();
            view.SelectedItems.Add(item);
        }

        _selectedItem = item;
        UpdateItemSelectionStates(view.SelectedItems.OfType<ExplorerItemViewModel>());
        _ = UpdateInspectorAsync(item);
    }

    private async void OnItemMenuOpenClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await OpenItemAsync(item);
        }
    }

    private async void OnItemMenuPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await OpenPreviewAsync(item, PreviewMode.WorkspaceQuickPreview);
        }
    }

    private void OnItemMenuOpenInExplorerClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            OpenInFileExplorer(item.FullPath);
        }
    }

    private void OnItemMenuRenameClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            BeginRename(item);
        }
    }

    private void OnItemMenuCopyPathClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is not { } item)
        {
            return;
        }

        var selected = ActiveFileView.SelectedItems.OfType<ExplorerItemViewModel>().ToArray();
        CopyPaths(selected.Contains(item) && selected.Length > 1 ? selected.Select(value => value.FullPath) : [item.FullPath]);
    }

    private async void OnItemMenuCopyClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is not { } item)
        {
            return;
        }

        var selected = GetSelectedItems();
        var items = selected.Contains(item) && selected.Length > 1
            ? selected
            : [item];
        await CopyItemsToClipboardAsync(items);
    }

    private async void OnCopySelectionClicked(object sender, RoutedEventArgs e) =>
        await CopyItemsToClipboardAsync(GetSelectedItems());

    private async void OnCopySelectionToClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems().FirstOrDefault() is { } item)
        {
            await TransferSelectionToPickedFolderAsync(item, FileTransferMode.Copy);
        }
    }

    private async void OnMoveSelectionToClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems().FirstOrDefault() is { } item)
        {
            await TransferSelectionToPickedFolderAsync(item, FileTransferMode.Move);
        }
    }

    private async void OnRecycleSelectionClicked(object sender, RoutedEventArgs e) =>
        await ConfirmRecycleAsync(GetSelectedItems().Select(item => item.FullPath));

    private async Task CopyItemsToClipboardAsync(
        IReadOnlyCollection<ExplorerItemViewModel> items)
    {
        if (_activeProject is null || items.Count == 0)
        {
            return;
        }

        try
        {
            var boundary = new PathBoundary(_activeProject.RootPath);
            var storageItems = new List<IStorageItem>(items.Count);
            foreach (var item in items)
            {
                var path = boundary.EnsureSafe(item.FullPath);
                storageItems.Add(item.IsDirectory
                    ? await StorageFolder.GetFolderFromPathAsync(path)
                    : await StorageFile.GetFileFromPathAsync(path));
            }

            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy
            };
            package.SetStorageItems(storageItems, readOnly: false);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            SetStatus($"已复制 {storageItems.Count} 个项目；可在目标文件夹按 Ctrl+V 粘贴");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            SetStatus($"无法复制到剪贴板：{exception.Message}");
        }
    }

    private async void OnBackgroundMenuPasteClicked(object sender, RoutedEventArgs e) =>
        await PasteClipboardAsync();

    private async Task PasteClipboardAsync()
    {
        if (_activeProject is null || _currentFolder is null)
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.StorageItems))
            {
                SetStatus("剪贴板中没有可粘贴的文件或文件夹");
                return;
            }

            var storageItems = await content.GetStorageItemsAsync();
            var paths = storageItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                SetStatus("剪贴板中没有可读取的本机项目");
                return;
            }

            var destination = new PathBoundary(_activeProject.RootPath).EnsureSafe(_currentFolder);
            var results = await ExecuteExternalImportAsync(paths, destination);
            if (results is null)
            {
                SetStatus("粘贴操作已取消");
                return;
            }

            if (results.Count > 0)
            {
                if (results.Any(result => result.ReplacedExisting))
                {
                    ClearUndo();
                }
                else
                {
                    var projectRoot = _activeProject.RootPath;
                    var undoResults = results.ToArray();
                    RegisterUndo("粘贴", () => UndoTransferAsync(projectRoot, undoResults));
                }
            }

            RefreshFileView();
            RebuildProjectTree();
            SetStatus(results.Count == 0
                ? "冲突项目已跳过，没有粘贴文件"
                : $"已粘贴 {results.Count} 个项目到 {GetFolderLabel(destination)}");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            SetStatus($"粘贴未完成：{exception.Message}");
        }
    }

    private async void OnItemMenuCopyToClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await TransferSelectionToPickedFolderAsync(item, FileTransferMode.Copy);
        }
    }

    private async void OnItemMenuMoveToClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { } item)
        {
            await TransferSelectionToPickedFolderAsync(item, FileTransferMode.Move);
        }
    }

    private async Task TransferSelectionToPickedFolderAsync(
        ExplorerItemViewModel contextItem,
        FileTransferMode mode)
    {
        if (_activeProject is null)
        {
            return;
        }

        var selected = ActiveFileView.SelectedItems.OfType<ExplorerItemViewModel>().ToArray();
        var paths = selected.Contains(contextItem) && selected.Length > 1
            ? selected.Select(item => item.FullPath).ToArray()
            : [contextItem.FullPath];

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
            CommitButtonText = mode == FileTransferMode.Copy ? "复制到这里" : "移动到这里"
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        try
        {
            var boundary = new PathBoundary(_activeProject.RootPath);
            var destination = boundary.EnsureSafe(folder.Path);
            if (!Directory.Exists(destination))
            {
                throw new DirectoryNotFoundException(destination);
            }

            var results = await ExecuteInternalTransferAsync(paths, destination, mode);
            if (results is null)
            {
                SetStatus("操作已取消");
                return;
            }

            var action = mode == FileTransferMode.Copy ? "复制" : "移动";
            if (results.Count > 0)
            {
                if (results.Any(result => result.ReplacedExisting))
                {
                    ClearUndo();
                }
                else
                {
                    var projectRoot = _activeProject.RootPath;
                    var undoResults = results.ToArray();
                    RegisterUndo(action, () => UndoTransferAsync(projectRoot, undoResults));
                }
            }

            RefreshFileView();
            RebuildProjectTree();
            SetStatus(results.Count == 0
                ? "冲突项目已跳过，没有文件发生变化"
                : $"已{action} {results.Count} 个项目到 {GetFolderLabel(destination)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            SetStatus($"操作未完成：{exception.Message}");
        }
    }

    private async void OnItemMenuRecycleClicked(object sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is not { } item)
        {
            return;
        }

        var selected = ActiveFileView.SelectedItems.OfType<ExplorerItemViewModel>().ToArray();
        var paths = selected.Contains(item) && selected.Length > 1
            ? selected.Select(value => value.FullPath)
            : [item.FullPath];
        await ConfirmRecycleAsync(paths);
    }

    private ExplorerItemViewModel? GetMenuItem(object sender) =>
        sender is MenuFlyoutItem { Tag: string fullPath } ? FindItem(fullPath) : null;

    private async void OnFileViewDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not ListViewBase source || source.SelectedItem is not ExplorerItemViewModel item)
        {
            return;
        }

        if (item.Item.IsDirectory)
        {
            NavigateTo(item.FullPath);
        }
        else
        {
            await OpenItemAsync(item);
        }
    }

    private async Task UpdateInspectorAsync(ExplorerItemViewModel? item)
    {
        UpdateInspector(item);

        if (item?.Item.IsImage == true)
        {
            await item.LoadThumbnailAsync();
            if (ReferenceEquals(_selectedItem, item) && item.Thumbnail is not null)
            {
                InspectorImage.Source = item.Thumbnail;
                InspectorImage.Visibility = Visibility.Visible;
                InspectorIcon.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void UpdateInspector(ExplorerItemViewModel? item)
    {
        if (item is null)
        {
            InspectorName.Text = "未选择文件";
            InspectorType.Text = "—";
            InspectorSize.Text = "—";
            InspectorModified.Text = "—";
            InspectorPath.Text = "—";
            InspectorImage.Source = null;
            InspectorImage.Visibility = Visibility.Collapsed;
            InspectorIcon.Visibility = Visibility.Visible;
            InspectorIcon.Glyph = "\uE7C3";
            return;
        }

        InspectorName.Text = item.Name;
        InspectorType.Text = item.DisplayType;
        InspectorSize.Text = string.IsNullOrWhiteSpace(item.SizeText) ? "—" : item.SizeText;
        InspectorModified.Text = item.ModifiedText;
        InspectorPath.Text = item.FullPath;
        InspectorIcon.Glyph = item.IconGlyph;
        InspectorIcon.Foreground = item.IconBrush;
        InspectorImage.Source = null;
        InspectorImage.Visibility = Visibility.Collapsed;
        InspectorIcon.Visibility = Visibility.Visible;
        SelectionStatusText.Text = item.Item.IsDirectory
            ? $"已选择文件夹 · {item.Name}"
            : $"已选择 1 个文件 · {item.SizeText}";
    }

    private async void OnOpenSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (_previewItem is not null && PreviewOverlay.Visibility == Visibility.Visible)
        {
            await OpenItemAsync(_previewItem);
        }
        else if (_selectedItem is not null)
        {
            await OpenItemAsync(_selectedItem);
        }
    }

    private async Task OpenItemAsync(ExplorerItemViewModel item)
    {
        try
        {
            if (item.Item.IsDirectory)
            {
                NavigateTo(item.FullPath);
                return;
            }

            if (_activeProject is null)
            {
                return;
            }

            var boundary = new PathBoundary(_activeProject.RootPath);
            var safePath = boundary.EnsureSafe(item.FullPath);
            var file = await StorageFile.GetFileFromPathAsync(safePath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"无法打开：{exception.Message}");
        }
    }

    private void OpenInFileExplorer(string path)
    {
        if (_activeProject is null)
        {
            return;
        }

        try
        {
            var boundary = new PathBoundary(_activeProject.RootPath);
            var safePath = boundary.EnsureSafe(path);
            var isDirectory = Directory.Exists(safePath);
            if (!isDirectory && !File.Exists(safePath))
            {
                throw new FileNotFoundException("文件或文件夹已经不存在。", safePath);
            }

            var arguments = isDirectory
                ? $"\"{safePath}\""
                : $"/select,\"{safePath}\"";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });

            if (process is null)
            {
                throw new InvalidOperationException("Windows 资源管理器未能启动。");
            }

            SetStatus(isDirectory
                ? $"已在资源管理器中打开：{Path.GetFileName(safePath)}"
                : $"已在资源管理器中定位：{Path.GetFileName(safePath)}");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            SetStatus($"无法在资源管理器中打开：{exception.Message}");
        }
    }

    private void OnCopyPathClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null)
        {
            return;
        }

        var selected = ActiveFileView.SelectedItems.OfType<ExplorerItemViewModel>().ToArray();
        CopyPaths(selected.Length > 1 ? selected.Select(item => item.FullPath) : [_selectedItem.FullPath]);
    }

    private async void OnRecycleSelectedClicked(object sender, RoutedEventArgs e)
    {
        var paths = ActiveFileView.SelectedItems
            .OfType<ExplorerItemViewModel>()
            .Select(item => item.FullPath)
            .ToArray();
        if (paths.Length == 0 && _selectedItem is not null)
        {
            paths = [_selectedItem.FullPath];
        }

        await ConfirmRecycleAsync(paths);
    }

    private async Task ConfirmRecycleAsync(IEnumerable<string> paths)
    {
        if (_activeProject is null)
        {
            return;
        }

        try
        {
            var projectRoot = _activeProject.RootPath;
            var planned = _fileOperations.PlanRecycle(projectRoot, paths);
            var names = string.Join(
                Environment.NewLine,
                planned.Take(5).Select(path => $"• {Path.GetFileName(path)}"));
            if (planned.Count > 5)
            {
                names += $"{Environment.NewLine}• 以及另外 {planned.Count - 5} 个项目";
            }

            var dialog = new ContentDialog
            {
                XamlRoot = RootLayout.XamlRoot,
                Title = planned.Count == 1 ? "移到回收站？" : $"将 {planned.Count} 个项目移到回收站？",
                Content = new TextBlock
                {
                    Text = $"{names}{Environment.NewLine}{Environment.NewLine}可以稍后从 Windows 回收站恢复。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 480,
                    Foreground = (Brush)Application.Current.Resources["HubTextSecondaryBrush"]
                },
                PrimaryButtonText = "移到回收站",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                SetStatus("删除操作已取消");
                return;
            }

            ClearUndo();
            var currentFolder = _currentFolder;
            _recycleBin.MoveToRecycleBin(projectRoot, planned);
            RebuildProjectTree();

            if (currentFolder is not null && planned.Any(path => IsSameOrDescendant(currentFolder, path)))
            {
                var fallback = planned
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault(path => path is not null && new PathBoundary(projectRoot).Contains(path));
                NavigateTo(fallback ?? projectRoot);
            }
            else
            {
                RefreshFileView();
            }

            SetStatus($"已将 {planned.Count} 个项目移到回收站");
        }
        catch (Exception exception)
        {
            AppDiagnostics.Log("Recycle operation failed", exception);
            SetStatus($"无法移到回收站：{exception.Message}");
        }
    }

    private static bool IsSameOrDescendant(string candidate, string directory)
    {
        var candidatePath = Path.GetFullPath(candidate);
        var directoryPath = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidatePath, directoryPath, StringComparison.OrdinalIgnoreCase)
            || candidatePath.StartsWith(directoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void CopyPaths(IEnumerable<string> paths)
    {
        var values = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, values));
        Clipboard.SetContent(package);
        SetStatus(values.Length == 1 ? "路径已复制" : $"已复制 {values.Length} 条路径");
    }

    private void RegisterUndo(string label, Func<Task> action)
    {
        _undoAction = action;
        UndoButtonText.Text = $"撤销 · {label}";
        UndoButton.Visibility = Visibility.Visible;
        UndoButton.IsEnabled = true;
    }

    private void ClearUndo()
    {
        _undoAction = null;
        UndoButton.Visibility = Visibility.Collapsed;
        UndoButton.IsEnabled = true;
    }

    private async void OnUndoClicked(object sender, RoutedEventArgs e)
    {
        if (_undoAction is null || _undoInProgress)
        {
            return;
        }

        _undoInProgress = true;
        UndoButton.IsEnabled = false;
        try
        {
            await _undoAction();
            ClearUndo();
            SetStatus("撤销已完成");
        }
        catch (Exception exception)
        {
            AppDiagnostics.Log("Undo failed", exception);
            UndoButton.IsEnabled = true;
            SetStatus($"无法撤销：{exception.Message}");
        }
        finally
        {
            _undoInProgress = false;
        }
    }

    private Task UndoRenameAsync(string projectRoot, FileOperationResult result)
    {
        EnsureUndoProject(projectRoot);
        var restored = _fileOperations.Rename(
            projectRoot,
            result.DestinationPath,
            Path.GetFileName(result.SourcePath));
        RefreshFileView();
        RebuildProjectTree();
        SelectPath(restored.DestinationPath);
        return Task.CompletedTask;
    }

    private Task UndoTransferAsync(string projectRoot, IReadOnlyList<FileOperationResult> results)
    {
        EnsureUndoProject(projectRoot);
        if (results.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (results.All(result => result.Mode == FileTransferMode.Copy))
        {
            _recycleBin.MoveToRecycleBin(projectRoot, results.Select(result => result.DestinationPath));
        }
        else
        {
            foreach (var result in results.Reverse())
            {
                var originalParent = Path.GetDirectoryName(result.SourcePath)
                    ?? throw new InvalidOperationException("无法确定原始位置。");
                _fileOperations.Transfer(
                    projectRoot,
                    [result.DestinationPath],
                    originalParent,
                    FileTransferMode.Move);
            }
        }

        RefreshFileView();
        RebuildProjectTree();
        return Task.CompletedTask;
    }

    private void EnsureUndoProject(string projectRoot)
    {
        if (_activeProject is null
            || !string.Equals(_activeProject.RootPath, projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("项目已经切换，不能在另一个项目中执行撤销。");
        }
    }

    private void BeginRename(ExplorerItemViewModel item)
    {
        foreach (var candidate in Items.Where(candidate => candidate.IsRenaming && !ReferenceEquals(candidate, item)))
        {
            candidate.CancelRename();
        }

        _pendingExtensionRenames.Remove(item.FullPath);
        _selectedItem = item;
        ActiveFileView.SelectedItem = item;
        item.BeginRename();
        FocusRenameBox(item);
    }

    private void FocusRenameBox(ExplorerItemViewModel item)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ActiveFileView.ScrollIntoView(item, ScrollIntoViewAlignment.Default);
            var container = ActiveFileView.ContainerFromItem(item);
            var textBox = FindVisualChild<TextBox>(container, control =>
                control.Tag is string path && string.Equals(path, item.FullPath, StringComparison.OrdinalIgnoreCase));

            if (textBox is null)
            {
                return;
            }

            textBox.Focus(FocusState.Programmatic);
            textBox.SelectionStart = 0;
            var extensionLength = item.IsDirectory ? 0 : Path.GetExtension(item.Name).Length;
            textBox.SelectionLength = Math.Max(0, item.Name.Length - extensionLength);
        });
    }

    private async void OnRenameBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string fullPath } textBox || FindItem(fullPath) is not { } item)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            _pendingExtensionRenames.Remove(item.FullPath);
            item.CancelRename();
            ActiveFileView.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync(item, textBox.Text);
        }
    }

    private async void OnRenameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_renameCommitInProgress || sender is not TextBox { Tag: string fullPath } textBox)
        {
            return;
        }

        if (FindItem(fullPath) is { IsRenaming: true } item)
        {
            await CommitRenameAsync(item, textBox.Text);
        }
    }

    private Task CommitRenameAsync(ExplorerItemViewModel item, string requestedName)
    {
        if (_activeProject is null || !item.IsRenaming || _renameCommitInProgress)
        {
            return Task.CompletedTask;
        }

        _renameCommitInProgress = true;
        item.RenameText = requestedName;

        try
        {
            FileOperationService.ValidateFileName(requestedName);

            var oldExtension = item.IsDirectory ? string.Empty : Path.GetExtension(item.Name);
            var newExtension = item.IsDirectory ? string.Empty : Path.GetExtension(requestedName);
            if (!string.Equals(oldExtension, newExtension, StringComparison.OrdinalIgnoreCase))
            {
                if (!_pendingExtensionRenames.TryGetValue(item.FullPath, out var pendingName)
                    || !string.Equals(pendingName, requestedName, StringComparison.Ordinal))
                {
                    _pendingExtensionRenames[item.FullPath] = requestedName;
                    item.SetRenameError("扩展名将改变，再按 Enter 确认");
                    SetStatus("扩展名变更需要再次确认");
                    FocusRenameBox(item);
                    return Task.CompletedTask;
                }
            }

            var projectRoot = _activeProject.RootPath;
            var result = _fileOperations.Rename(projectRoot, item.FullPath, requestedName);
            _pendingExtensionRenames.Remove(item.FullPath);
            RegisterUndo(
                "重命名",
                () => UndoRenameAsync(projectRoot, result));
            RefreshFileView();
            SelectPath(result.DestinationPath);
            RebuildProjectTree();
            SetStatus($"已重命名为 {Path.GetFileName(result.DestinationPath)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            item.SetRenameError(exception.Message);
            SetStatus($"无法重命名：{exception.Message}");
            FocusRenameBox(item);
        }
        finally
        {
            _renameCommitInProgress = false;
        }

        return Task.CompletedTask;
    }

    private void SelectPath(string fullPath)
    {
        if (FindItem(fullPath) is not { } item)
        {
            return;
        }

        _selectedItem = item;
        ActiveFileView.SelectedItem = item;
        ActiveFileView.ScrollIntoView(item, ScrollIntoViewAlignment.Default);
        _ = UpdateInspectorAsync(item);
    }

    private static T? FindVisualChild<T>(DependencyObject? parent, Func<T, bool> predicate)
        where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild && predicate(typedChild))
            {
                return typedChild;
            }

            var nested = FindVisualChild(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            return;
        }

        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == VirtualKey.Escape)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }

            return;
        }

        if (PreviewOverlay.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case VirtualKey.Space:
                case VirtualKey.Escape:
                    ClosePreview();
                    e.Handled = true;
                    return;
                case VirtualKey.Left:
                    await StepPreviewAsync(-1);
                    e.Handled = true;
                    return;
                case VirtualKey.Right:
                    await StepPreviewAsync(1);
                    e.Handled = true;
                    return;
            }
        }

        var controlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                           & CoreVirtualKeyStates.Down) != 0;
        if (controlDown && e.Key == VirtualKey.A)
        {
            SelectAllItems();
            e.Handled = true;
            return;
        }

        if (controlDown && e.Key == VirtualKey.C && GetSelectedItems().Length > 0)
        {
            await CopyItemsToClipboardAsync(GetSelectedItems());
            e.Handled = true;
            return;
        }

        if (controlDown && e.Key == VirtualKey.V)
        {
            await PasteClipboardAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape
            && (_multiSelectMode || GetSelectedItems().Length > 0))
        {
            SetMultiSelectMode(false, clearSelectionWhenDisabled: true);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Space && _selectedItem is not null)
        {
            if (_settingsState.SpacePreviewEnabled)
            {
                await OpenPreviewAsync(_selectedItem, PreviewMode.SingleQuickLook);
            }
            else
            {
                SetStatus("Space 单文件预览已在设置中关闭");
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && _selectedItem is not null)
        {
            await OpenItemAsync(_selectedItem);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F2 && _selectedItem is not null)
        {
            BeginRename(_selectedItem);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete && _selectedItem is not null)
        {
            var selectedPaths = ActiveFileView.SelectedItems
                .OfType<ExplorerItemViewModel>()
                .Select(item => item.FullPath)
                .ToArray();
            await ConfirmRecycleAsync(selectedPaths.Length > 0 ? selectedPaths : [_selectedItem.FullPath]);
            e.Handled = true;
        }
    }

    private async Task OpenPreviewAsync(ExplorerItemViewModel item, PreviewMode mode)
    {
        _previewMode = mode;
        ConfigurePreviewPresentation(mode);
        PreviewItems.Clear();
        foreach (var candidate in Items)
        {
            PreviewItems.Add(candidate);
        }

        if (!PreviewItems.Contains(item))
        {
            PreviewItems.Add(item);
        }

        PreviewOverlay.Visibility = Visibility.Visible;
        await ShowPreviewItemAsync(item);
    }

    private void ConfigurePreviewPresentation(PreviewMode mode)
    {
        var isSingleQuickLook = mode == PreviewMode.SingleQuickLook;
        PreviewOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
        PreviewOverlay.VerticalAlignment = VerticalAlignment.Stretch;
        PreviewOverlay.Margin = new Thickness(0);
        PreviewOverlay.MaxWidth = double.PositiveInfinity;
        PreviewOverlay.MaxHeight = double.PositiveInfinity;
        PreviewFilmstrip.Visibility = isSingleQuickLook ? Visibility.Collapsed : Visibility.Visible;
        PreviewFilmstripRow.Height = isSingleQuickLook
            ? new GridLength(0)
            : new GridLength(142);
        PreviewShortcutHint.Text = isSingleQuickLook
            ? "Hub 全屏预览    ·    ←  →  切换    ·    图片滚轮缩放 / 拖动    ·    Space / Esc  关闭"
            : "←  →  切换    ·    图片滚轮缩放 / 拖动    ·    Space / Esc  关闭";
        SinglePreviewScrim.Visibility = isSingleQuickLook ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowPreviewItemAsync(ExplorerItemViewModel item)
    {
        var previewVersion = ++_previewVersion;
        _previewItem = item;
        PreviewTitle.Text = item.Name;
        PreviewType.Text = item.DisplayType;
        PreviewSize.Text = item.SizeText;
        PreviewPath.Text = item.FullPath;
        ResetPreviewContent();
        UpdatePreviewNavigationState();

        if (item.Item.IsDirectory)
        {
            ShowFolderPreview(item);
        }
        else if (item.Item.IsImage)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                if (previewVersion != _previewVersion)
                {
                    return;
                }

                PreviewImage.Source = bitmap;
                ShowImagePreviewControls();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowPreviewFallback(item, "Windows 无法解码这个图像文件");
            }
        }
        else if (IsTextPreviewSupported(item.Item.Extension))
        {
            await ShowTextPreviewAsync(item, previewVersion);
        }
        else if (item.Item.Category is FileItemCategory.Audio or FileItemCategory.Video)
        {
            await ShowMediaPreviewAsync(item, previewVersion);
        }
        else if (!await TryShowSystemThumbnailAsync(item, previewVersion))
        {
            ShowPreviewFallback(item, "Windows 暂时没有为此文件提供可显示的预览\n可使用下方按钮在默认应用中打开");
        }
        else
        {
            // The Windows shell thumbnail is already visible.
        }

        _synchronizingPreview = true;
        PreviewFilmstrip.SelectedItem = item;
        PreviewFilmstrip.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        _synchronizingPreview = false;
    }

    private void ResetPreviewContent()
    {
        CancelPreviewImagePan();
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewImageScroll.Visibility = Visibility.Collapsed;
        PreviewZoomResetButton.Visibility = Visibility.Collapsed;
        ResetPreviewZoom();
        PreviewMedia.Source = null;
        PreviewMedia.Visibility = Visibility.Collapsed;
        PreviewText.Text = string.Empty;
        PreviewTextScroll.Visibility = Visibility.Collapsed;
        PreviewCodeText.Blocks.Clear();
        PreviewCodeLineNumbers.Text = string.Empty;
        PreviewCodeLanguage.Text = string.Empty;
        PreviewCodeSurface.Visibility = Visibility.Collapsed;
        PreviewMarkdownDocument.Children.Clear();
        PreviewMarkdownScroll.Visibility = Visibility.Collapsed;
        PreviewWrapButton.Visibility = Visibility.Collapsed;
        PreviewFallback.Visibility = Visibility.Collapsed;
    }

    private void ShowFolderPreview(ExplorerItemViewModel item)
    {
        if (_activeProject is null)
        {
            ShowPreviewFallback(item, "当前没有活动项目");
            return;
        }

        try
        {
            var children = _fileBrowser.GetItems(
                _activeProject.RootPath,
                item.FullPath,
                new FileQueryOptions(FileSortField.Name, SortDirection.Ascending));
            var folderCount = children.Count(child => child.IsDirectory);
            var fileCount = children.Count - folderCount;
            var directSize = children.Where(child => !child.IsDirectory).Sum(child => child.Size ?? 0);
            var relative = Path.GetRelativePath(_activeProject.RootPath, item.FullPath);

            PreviewText.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            PreviewText.TextWrapping = TextWrapping.Wrap;
            PreviewTextScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            PreviewText.Text = $"{item.Name}\n\n直接包含  {folderCount} 个文件夹  ·  {fileCount} 个文件\n当前层文件大小  {ExplorerItemViewModel.FormatBytes(directSize)}\n最后修改  {item.ModifiedText}\n项目内位置  {relative}\n\nEnter 或双击可进入此文件夹";
            PreviewTextScroll.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowPreviewFallback(item, $"无法读取文件夹摘要\n{exception.Message}");
        }
    }

    private async Task ShowTextPreviewAsync(ExplorerItemViewModel item, int previewVersion)
    {
        const long maximumPreviewBytes = 1_500_000;
        try
        {
            var info = new FileInfo(item.FullPath);
            if (info.Length > maximumPreviewBytes)
            {
                ShowPreviewFallback(item, $"文本文件较大（{ExplorerItemViewModel.FormatBytes(info.Length)}）\n为保持预览流畅，请使用默认应用打开");
                return;
            }

            var text = await File.ReadAllTextAsync(item.FullPath);
            if (previewVersion != _previewVersion)
            {
                return;
            }

            PreviewWrapButton.IsChecked = _previewTextWrapEnabled;
            PreviewWrapButton.Visibility = Visibility.Visible;
            if (IsMarkdownExtension(item.Item.Extension))
            {
                RenderMarkdownPreview(text);
                PreviewMarkdownScroll.Visibility = Visibility.Visible;
            }
            else if (IsCodePreviewSupported(item.Item.Extension))
            {
                RenderCodePreview(text, item.Item.Extension);
                PreviewCodeSurface.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                PreviewText.Text = text.Length == 0 ? "（空文件）" : text;
                PreviewTextScroll.Visibility = Visibility.Visible;
            }

            ApplyPreviewWrapMode();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowPreviewFallback(item, $"无法读取文本预览\n{exception.Message}");
        }
    }

    private async Task ShowMediaPreviewAsync(ExplorerItemViewModel item, int previewVersion)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
            if (previewVersion != _previewVersion)
            {
                return;
            }

            PreviewMedia.Source = MediaSource.CreateFromStorageFile(file);
            PreviewMedia.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowPreviewFallback(item, $"Windows 无法加载这个媒体文件\n{exception.Message}");
        }
    }

    private async Task<bool> TryShowSystemThumbnailAsync(ExplorerItemViewModel item, int previewVersion)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FullPath);
            using var thumbnail = await file.GetThumbnailAsync(
                Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                1600,
                Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
            if (previewVersion != _previewVersion || thumbnail.Size == 0)
            {
                return false;
            }

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            if (previewVersion != _previewVersion)
            {
                return false;
            }

            PreviewImage.Source = bitmap;
            ShowImagePreviewControls();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsTextPreviewSupported(string extension) => extension.ToLowerInvariant() switch
    {
        ".c" or ".cpp" or ".cs" or ".css" or ".csv" or ".go" or ".h" or ".hpp" or ".html" or
        ".java" or ".js" or ".json" or ".jsx" or ".kt" or ".lua" or ".md" or ".markdown" or ".php" or ".ps1" or
        ".py" or ".rb" or ".rs" or ".sql" or ".swift" or ".ts" or ".tsx" or ".txt" or ".xml" or
        ".xaml" or ".yaml" or ".yml" => true,
        _ => false
    };

    private static bool IsMarkdownExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".md" or ".markdown" => true,
        _ => false
    };

    private static bool IsCodePreviewSupported(string extension) => extension.ToLowerInvariant() switch
    {
        ".c" or ".cpp" or ".cs" or ".css" or ".go" or ".h" or ".hpp" or ".html" or
        ".java" or ".js" or ".json" or ".jsx" or ".kt" or ".lua" or ".php" or ".ps1" or
        ".py" or ".rb" or ".rs" or ".sql" or ".swift" or ".ts" or ".tsx" or ".xml" or
        ".xaml" or ".yaml" or ".yml" => true,
        _ => false
    };

    private void RenderCodePreview(string source, string extension)
    {
        PreviewCodeText.Blocks.Clear();
        PreviewCodeLanguage.Text = extension.TrimStart('.').ToUpperInvariant();

        var text = source.Length == 0 ? "（空文件）" : source;
        var paragraph = new Paragraph();
        foreach (var token in CodePreviewTokenizer.Tokenize(text))
        {
            paragraph.Inlines.Add(new Run
            {
                Text = token.Text,
                Foreground = token.Kind switch
                {
                    CodePreviewTokenKind.Comment => CodeCommentBrush,
                    CodePreviewTokenKind.String => CodeStringBrush,
                    CodePreviewTokenKind.Number => CodeNumberBrush,
                    CodePreviewTokenKind.Keyword => CodeKeywordBrush,
                    _ => CodePlainBrush
                }
            });
        }

        PreviewCodeText.Blocks.Add(paragraph);
        var lineCount = text.Count(character => character == '\n') + 1;
        PreviewCodeLineNumbers.Text = string.Join(Environment.NewLine, Enumerable.Range(1, lineCount));
    }

    private void RenderMarkdownPreview(string markdown)
    {
        PreviewMarkdownDocument.Children.Clear();
        var blocks = MarkdownPreviewParser.Parse(markdown);
        if (blocks.Count == 0)
        {
            PreviewMarkdownDocument.Children.Add(CreateMarkdownTextBlock(
                "（空文件）",
                15,
                FontWeights.Normal,
                (Brush)Application.Current.Resources["HubTextMutedBrush"]));
            return;
        }

        foreach (var block in blocks)
        {
            UIElement element = block.Kind switch
            {
                MarkdownPreviewBlockKind.Heading => CreateMarkdownHeading(block),
                MarkdownPreviewBlockKind.BulletListItem => CreateMarkdownListItem(block, ordered: false),
                MarkdownPreviewBlockKind.NumberedListItem => CreateMarkdownListItem(block, ordered: true),
                MarkdownPreviewBlockKind.Quote => CreateMarkdownQuote(block),
                MarkdownPreviewBlockKind.Code => CreateMarkdownCodeBlock(block),
                MarkdownPreviewBlockKind.HorizontalRule => new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 14, 0, 14),
                    Background = (Brush)Application.Current.Resources["HubBorderBrush"]
                },
                _ => CreateMarkdownParagraph(block)
            };
            PreviewMarkdownDocument.Children.Add(element);
        }
    }

    private TextBlock CreateMarkdownHeading(MarkdownPreviewBlock block)
    {
        var fontSize = block.Level switch
        {
            1 => 32d,
            2 => 26d,
            3 => 22d,
            4 => 19d,
            5 => 16d,
            _ => 14d
        };
        var heading = CreateMarkdownTextBlock(
            block.Text,
            fontSize,
            block.Level <= 2 ? FontWeights.Bold : FontWeights.SemiBold,
            (Brush)Application.Current.Resources["HubTextBrush"]);
        heading.LineHeight = fontSize * 1.3;
        heading.Margin = new Thickness(0, block.Level == 1 ? 0 : 18, 0, 7);
        return heading;
    }

    private TextBlock CreateMarkdownParagraph(MarkdownPreviewBlock block)
    {
        var paragraph = CreateMarkdownTextBlock(
            block.Text,
            15,
            FontWeights.Normal,
            (Brush)Application.Current.Resources["HubTextSecondaryBrush"]);
        paragraph.LineHeight = 25;
        paragraph.Margin = new Thickness(0, 2, 0, 8);
        return paragraph;
    }

    private Grid CreateMarkdownListItem(MarkdownPreviewBlock block, bool ordered)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 2, 0, 4)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = block.IsChecked switch
        {
            true => "✓",
            false => "□",
            null => ordered ? block.Marker ?? "1." : "•"
        };
        row.Children.Add(new TextBlock
        {
            Text = marker,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["HubAccentStrongBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0)
        });

        var content = CreateMarkdownTextBlock(
            block.Text,
            15,
            FontWeights.Normal,
            (Brush)Application.Current.Resources["HubTextSecondaryBrush"]);
        content.LineHeight = 24;
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        return row;
    }

    private Border CreateMarkdownQuote(MarkdownPreviewBlock block)
    {
        var text = CreateMarkdownTextBlock(
            block.Text,
            15,
            FontWeights.Normal,
            (Brush)Application.Current.Resources["HubTextSecondaryBrush"]);
        text.LineHeight = 24;
        return new Border
        {
            Margin = new Thickness(0, 7, 0, 11),
            Padding = new Thickness(16, 11, 16, 11),
            Background = (Brush)Application.Current.Resources["HubRaisedBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HubAccentBrush"],
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 7, 7, 0),
            Child = text
        };
    }

    private Border CreateMarkdownCodeBlock(MarkdownPreviewBlock block)
    {
        var codeText = new TextBlock
        {
            Text = block.Text.Length == 0 ? " " : block.Text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            LineHeight = 21,
            IsTextSelectionEnabled = true,
            TextWrapping = _previewTextWrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Foreground = (Brush)Application.Current.Resources["HubTextSecondaryBrush"]
        };
        var body = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(block.Language))
        {
            body.Children.Add(new TextBlock
            {
                Text = block.Language.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["HubAccentStrongBrush"]
            });
        }

        body.Children.Add(codeText);
        return new Border
        {
            Margin = new Thickness(0, 8, 0, 12),
            Padding = new Thickness(16, 13, 16, 14),
            Background = (Brush)Application.Current.Resources["HubRaisedBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HubBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = body
        };
    }

    private TextBlock CreateMarkdownTextBlock(
        string text,
        double fontSize,
        FontWeight fontWeight,
        Brush foreground)
    {
        var textBlock = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = foreground,
            IsTextSelectionEnabled = true,
            TextWrapping = _previewTextWrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        AddMarkdownInlines(textBlock, text);
        return textBlock;
    }

    private static void AddMarkdownInlines(TextBlock textBlock, string text)
    {
        var position = 0;
        foreach (Match match in MarkdownInlineRegex.Matches(text))
        {
            if (match.Index > position)
            {
                textBlock.Inlines.Add(new Run { Text = text[position..match.Index] });
            }

            var token = match.Value;
            if ((token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal))
                || (token.StartsWith("__", StringComparison.Ordinal) && token.EndsWith("__", StringComparison.Ordinal)))
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run { Text = token[2..^2] });
                textBlock.Inlines.Add(bold);
            }
            else if (token.StartsWith('`') && token.EndsWith('`'))
            {
                textBlock.Inlines.Add(new Run
                {
                    Text = token[1..^1],
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = (Brush)Application.Current.Resources["HubAccentStrongBrush"]
                });
            }
            else if (token.StartsWith('['))
            {
                var labelEnd = token.IndexOf("](", StringComparison.Ordinal);
                var underline = new Underline();
                underline.Inlines.Add(new Run
                {
                    Text = labelEnd > 1 ? token[1..labelEnd] : token,
                    Foreground = (Brush)Application.Current.Resources["HubAccentStrongBrush"]
                });
                textBlock.Inlines.Add(underline);
            }
            else
            {
                var italic = new Italic();
                italic.Inlines.Add(new Run { Text = token[1..^1] });
                textBlock.Inlines.Add(italic);
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            textBlock.Inlines.Add(new Run { Text = text[position..] });
        }
    }

    private void OnPreviewWrapClicked(object sender, RoutedEventArgs e)
    {
        _previewTextWrapEnabled = PreviewWrapButton.IsChecked == true;
        ApplyPreviewWrapMode();
    }

    private void ApplyPreviewWrapMode()
    {
        var wrapping = _previewTextWrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PreviewWrapLabel.Text = _previewTextWrapEnabled ? "自动换行：开" : "自动换行：关";
        PreviewText.TextWrapping = wrapping;
        PreviewTextScroll.HorizontalScrollBarVisibility = _previewTextWrapEnabled
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        PreviewCodeText.TextWrapping = wrapping;
        PreviewCodeScroll.HorizontalScrollBarVisibility = _previewTextWrapEnabled
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        PreviewCodeLineNumbers.Visibility = _previewTextWrapEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;
        PreviewMarkdownScroll.HorizontalScrollBarVisibility = _previewTextWrapEnabled
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        PreviewMarkdownPage.MaxWidth = _previewTextWrapEnabled ? 960 : double.PositiveInfinity;
        PreviewMarkdownPage.HorizontalAlignment = _previewTextWrapEnabled
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Left;
        ApplyTextWrapping(PreviewMarkdownDocument, wrapping);
    }

    private static void ApplyTextWrapping(DependencyObject parent, TextWrapping wrapping)
    {
        if (parent is TextBlock textBlock)
        {
            textBlock.TextWrapping = wrapping;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            ApplyTextWrapping(VisualTreeHelper.GetChild(parent, index), wrapping);
        }
    }

    private void ShowPreviewFallback(ExplorerItemViewModel item, string message)
    {
        PreviewFallbackIcon.Glyph = item.IconGlyph;
        PreviewFallbackIcon.Foreground = item.IconBrush;
        PreviewFallbackText.Text = message;
        PreviewFallback.Visibility = Visibility.Visible;
    }

    private async Task StepPreviewAsync(int delta)
    {
        if (_previewItem is null || PreviewItems.Count == 0)
        {
            return;
        }

        var currentIndex = PreviewItems.IndexOf(_previewItem);
        var nextIndex = Math.Clamp(currentIndex + delta, 0, PreviewItems.Count - 1);

        if (nextIndex != currentIndex)
        {
            await ShowPreviewItemAsync(PreviewItems[nextIndex]);
        }

        UpdatePreviewNavigationState();
    }

    private void UpdatePreviewNavigationState()
    {
        var currentIndex = _previewItem is null ? -1 : PreviewItems.IndexOf(_previewItem);
        var showNavigation = PreviewItems.Count > 1 && currentIndex >= 0;
        var visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;

        PreviewPreviousButton.Visibility = visibility;
        PreviewNextButton.Visibility = visibility;
        PreviewPreviousButton.IsEnabled = showNavigation && currentIndex > 0;
        PreviewNextButton.IsEnabled = showNavigation && currentIndex < PreviewItems.Count - 1;
    }

    private async void OnPreviewPreviousClicked(object sender, RoutedEventArgs e) =>
        await StepPreviewAsync(-1);

    private async void OnPreviewNextClicked(object sender, RoutedEventArgs e) =>
        await StepPreviewAsync(1);

    private void ShowImagePreviewControls()
    {
        PreviewImage.Visibility = Visibility.Visible;
        PreviewImageScroll.Visibility = Visibility.Visible;
        PreviewZoomResetButton.Visibility = Visibility.Visible;
        UpdatePreviewImageCanvasSize();
    }

    private void OnPreviewImageScrollSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePreviewImageCanvasSize();

    private void UpdatePreviewImageCanvasSize()
    {
        if (PreviewImageScroll.ActualWidth <= 0 || PreviewImageScroll.ActualHeight <= 0)
        {
            return;
        }

        PreviewImageCanvas.Width = PreviewImageScroll.ActualWidth;
        PreviewImageCanvas.Height = PreviewImageScroll.ActualHeight;
    }

    private void OnPreviewImagePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (PreviewImageScroll.Visibility != Visibility.Visible || PreviewImage.Source is null)
        {
            return;
        }

        var wheelDelta = e.GetCurrentPoint(PreviewImageScroll).Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        var wheelSteps = wheelDelta / 120.0;
        var currentZoom = Math.Max(PreviewImageScroll.ZoomFactor, PreviewZoomMinimum);
        var targetZoom = currentZoom * Math.Pow(PreviewZoomStep, wheelSteps);
        SetPreviewZoom((float)targetZoom, disableAnimation: true);
        e.Handled = true;
    }

    private void OnPreviewImagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PreviewImageScroll);
        if (!point.Properties.IsLeftButtonPressed
            || PreviewImageScroll.ZoomFactor <= 1.001f
            || (PreviewImageScroll.ScrollableWidth <= 0.5
                && PreviewImageScroll.ScrollableHeight <= 0.5)
            || !PreviewImageCanvas.CapturePointer(e.Pointer))
        {
            return;
        }

        _isPreviewImagePanning = true;
        _previewImagePanPointerId = e.Pointer.PointerId;
        _previewImagePanStartX = point.Position.X;
        _previewImagePanStartY = point.Position.Y;
        _previewImagePanStartHorizontalOffset = PreviewImageScroll.HorizontalOffset;
        _previewImagePanStartVerticalOffset = PreviewImageScroll.VerticalOffset;
        e.Handled = true;
    }

    private void OnPreviewImagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPreviewImagePanning || e.Pointer.PointerId != _previewImagePanPointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(PreviewImageScroll);
        if (!point.Properties.IsLeftButtonPressed)
        {
            FinishPreviewImagePan(e.Pointer);
            return;
        }

        var horizontalOffset = PreviewZoomMath.CalculatePanOffset(
            _previewImagePanStartHorizontalOffset,
            point.Position.X - _previewImagePanStartX,
            PreviewImageScroll.ScrollableWidth);
        var verticalOffset = PreviewZoomMath.CalculatePanOffset(
            _previewImagePanStartVerticalOffset,
            point.Position.Y - _previewImagePanStartY,
            PreviewImageScroll.ScrollableHeight);
        PreviewImageScroll.ChangeView(horizontalOffset, verticalOffset, null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnPreviewImagePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPreviewImagePanning && e.Pointer.PointerId == _previewImagePanPointerId)
        {
            FinishPreviewImagePan(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPreviewImagePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isPreviewImagePanning && e.Pointer.PointerId == _previewImagePanPointerId)
        {
            FinishPreviewImagePan(e.Pointer);
        }
    }

    private void OnPreviewImagePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _previewImagePanPointerId)
        {
            _isPreviewImagePanning = false;
            _previewImagePanPointerId = 0;
        }
    }

    private void FinishPreviewImagePan(Pointer pointer)
    {
        _isPreviewImagePanning = false;
        _previewImagePanPointerId = 0;
        PreviewImageCanvas.ReleasePointerCapture(pointer);
    }

    private void CancelPreviewImagePan()
    {
        if (!_isPreviewImagePanning)
        {
            return;
        }

        _isPreviewImagePanning = false;
        _previewImagePanPointerId = 0;
        PreviewImageCanvas.ReleasePointerCaptures();
    }

    private void OnPreviewZoomResetClicked(object sender, RoutedEventArgs e) => ResetPreviewZoom();

    private void ResetPreviewZoom() => SetPreviewZoom(1.0f, disableAnimation: true);

    private void SetPreviewZoom(float zoomFactor, bool disableAnimation = false)
    {
        CancelPreviewImagePan();
        var viewportWidth = PreviewImageScroll.ViewportWidth > 0
            ? PreviewImageScroll.ViewportWidth
            : PreviewImageScroll.ActualWidth;
        var viewportHeight = PreviewImageScroll.ViewportHeight > 0
            ? PreviewImageScroll.ViewportHeight
            : PreviewImageScroll.ActualHeight;
        var contentWidth = PreviewImageCanvas.Width > 0
            ? PreviewImageCanvas.Width
            : viewportWidth;
        var contentHeight = PreviewImageCanvas.Height > 0
            ? PreviewImageCanvas.Height
            : viewportHeight;
        var view = PreviewZoomMath.CalculateCenteredView(
            PreviewImageScroll.HorizontalOffset,
            PreviewImageScroll.VerticalOffset,
            viewportWidth,
            viewportHeight,
            contentWidth,
            contentHeight,
            Math.Max(PreviewImageScroll.ZoomFactor, PreviewZoomMinimum),
            zoomFactor,
            PreviewZoomMinimum,
            PreviewZoomMaximum);

        _previewZoomFactor = view.ZoomFactor;
        PreviewZoomLabel.Text = $"{Math.Round(_previewZoomFactor * 100):0}%";
        PreviewImageScroll.ChangeView(
            view.HorizontalOffset,
            view.VerticalOffset,
            view.ZoomFactor,
            disableAnimation);
    }

    private async void OnPreviewFilmstripSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingPreview || PreviewFilmstrip.SelectedItem is not ExplorerItemViewModel item)
        {
            return;
        }

        await ShowPreviewItemAsync(item);
    }

    private void OnClosePreviewClicked(object sender, RoutedEventArgs e) => ClosePreview();

    private void OnSinglePreviewScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_previewMode == PreviewMode.SingleQuickLook)
        {
            ClosePreview();
        }
    }

    private void ClosePreview()
    {
        _previewVersion++;
        PreviewOverlay.Visibility = Visibility.Collapsed;
        SinglePreviewScrim.Visibility = Visibility.Collapsed;
        ResetPreviewContent();
        _previewItem = null;
    }

    private void SetStatus(string message)
    {
        SelectionStatusText.Text = message;
    }
}
