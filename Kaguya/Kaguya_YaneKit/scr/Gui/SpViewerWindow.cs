// ============================================================================
// SpViewerWindow.cs
// SP 立绘查看器主窗口 (code-only Avalonia, 无 XAML)
//
// 布局:
//   左侧: Fluent 风格工作台, 包含角色/差分/叠加/背景选择卡片
//   右侧: 大预览画布, 顶部信息栏, 底部导出和状态栏
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Kaguya_YaneKit.Formats.Character;
using Kaguya_YaneKit.Formats.Params;
using AvBrushes = Avalonia.Media.Brushes;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;
using SysBitmap = System.Drawing.Bitmap;
using SysColor = System.Drawing.Color;

namespace Kaguya_YaneKit.Gui;

internal sealed class SpViewerWindow : Window
{
    private IBrush WindowBrush => _isDarkTheme ? Brush(18, 18, 20) : Brush(246, 241, 229);
    private IBrush SurfaceBrush => _isDarkTheme
        ? (HasToolBackground ? Brush(232, 31, 31, 35) : Brush(31, 31, 35))
        : (HasToolBackground ? Brush(236, 255, 251, 241) : Brush(255, 251, 241));
    private IBrush SurfaceAltBrush => _isDarkTheme
        ? (HasToolBackground ? Brush(224, 38, 38, 43) : Brush(38, 38, 43))
        : (HasToolBackground ? Brush(228, 250, 244, 232) : Brush(250, 244, 232));
    private IBrush PanelBrush => _isDarkTheme
        ? (HasToolBackground ? Brush(226, 25, 25, 29) : Brush(25, 25, 29))
        : (HasToolBackground ? Brush(232, 240, 233, 219) : Brush(240, 233, 219));
    private IBrush BorderLineBrush => _isDarkTheme ? Brush(62, 62, 70) : Brush(222, 211, 193);
    private IBrush TextBrush => _isDarkTheme ? AvBrushes.White : Brush(42, 37, 31);
    private IBrush MutedTextBrush => _isDarkTheme ? Brush(166, 166, 174) : Brush(104, 91, 76);
    private bool HasToolBackground => !string.IsNullOrEmpty(_toolBackgroundPath) && File.Exists(_toolBackgroundPath);

    private readonly string _picDir;
    private readonly SpViewerSource _source;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private readonly ListBox _characterList;
    private readonly ListBox _expressionList;
    private readonly ListBox _overlayList;
    private readonly ListBox _backgroundList;
    private readonly ListBox _customBgList;
    private readonly TextBox _variantSearchBox;
    private readonly TextBox _backgroundSearchBox;
    private readonly TextBox _customBgSearchBox;
    private readonly Button _customBgBrowseBtn;
    private readonly TextBlock _customBgPathText;
    private readonly Avalonia.Controls.Image _previewImage;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly Button _exportCurrentBtn;
    private readonly Button _exportAllBtn;
    private readonly Button _themeToggleBtn;
    private readonly Button _toolBgBrowseBtn;
    private readonly Button _toolBgClearBtn;
    private readonly List<Border> _surfaceCards = new();
    private readonly List<Border> _surfaceAltCards = new();
    private readonly List<Border> _panelCards = new();
    private readonly List<TextBlock> _titleTexts = new();
    private readonly List<TextBlock> _captionTexts = new();
    private Grid? _rootLayer;
    private Avalonia.Controls.Image? _toolBackgroundImage;
    private Border? _toolBackgroundOverlay;

    private List<SpCharacterGroup> _characters = new();
    private List<SpExpressionEntry> _overlays = new();
    private List<SpBackgroundEntry> _backgrounds = new();
    private List<SpBackgroundEntry> _customBgs = new();
    private bool _isExporting;
    private bool _suppressBgSync;
    private bool _isDarkTheme = true;
    private string? _toolBackgroundPath;

    private string ConfigRoot => Path.GetFullPath(Path.Combine(_picDir, ".."));
    private string ConfigPath => Path.Combine(ConfigRoot, "config.json");

    public SpViewerWindow(string picDir, SpViewerSource source, int canvasWidth, int canvasHeight)
    {
        _picDir = Path.GetFullPath(picDir);
        _source = source;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;

        Title = "Kaguya SP Viewer";
        Width = 1180;
        Height = 780;
        MinWidth = 980;
        MinHeight = 640;
        Background = WindowBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _characterList = CreateListBox(120);
        _expressionList = CreateListBox(145);
        _variantSearchBox = new TextBox
        {
            Watermark = "Search variant...",
            Margin = new Thickness(0, 0, 0, 7),
            Padding = new Thickness(8, 4),
            Background = SurfaceAltBrush,
            Foreground = TextBrush,
            BorderBrush = BorderLineBrush,
            BorderThickness = new Thickness(1)
        };
        _overlayList = new ListBox
        {
            SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle,
            BorderBrush = BorderLineBrush, BorderThickness = new Thickness(1), MaxHeight = 110,
            Background = SurfaceAltBrush, Foreground = TextBrush,
            Padding = new Thickness(2),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = CreateListItemTemplate()
        };
        _backgroundList = CreateListBox(105);
        _customBgList = CreateListBox(105);
        _backgroundSearchBox = CreateSearchBox("Search background...");
        _customBgSearchBox = CreateSearchBox("Search custom BG...");
        _customBgBrowseBtn = new Button { Content = "Browse...", Margin = new Thickness(0, 2, 8, 2), Padding = new Thickness(10, 4) };
        _customBgPathText = new TextBlock
        {
            Text = "(no folder)",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MutedTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 170
        };
        _previewImage = new Avalonia.Controls.Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusText = new TextBlock
        {
            Text = "Loading...",
            Margin = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MutedTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        _progressBar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Height = 18,
            IsIndeterminate = true, IsVisible = true,
            Margin = new Thickness(8, 0)
        };
        _exportCurrentBtn = new Button { Content = "Export Current", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6), IsEnabled = false };
        _exportAllBtn = new Button { Content = "Export Character", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6), IsEnabled = false };
        _themeToggleBtn = new Button { Content = "Light", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6) };
        _toolBgBrowseBtn = new Button { Content = "Tool BG", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6) };
        _toolBgClearBtn = new Button { Content = "Clear BG", Padding = new Thickness(12, 6), IsVisible = false };

        LoadUiConfig();
        ApplyThemeVariant();
        UpdateChromeState();
        RefreshControlTheme();
        Content = BuildLayout();

        _characterList.SelectionChanged += (_, _) => OnCharacterChanged();
        _expressionList.SelectionChanged += (_, _) => OnSelectionChanged();
        _variantSearchBox.TextChanged += (_, _) => ApplyExpressionFilter(keepSelection: true);
        _overlayList.SelectionChanged += (_, _) => OnSelectionChanged();
        _backgroundSearchBox.TextChanged += (_, _) => ApplyBackgroundFilter(keepSelection: true);
        _customBgSearchBox.TextChanged += (_, _) => ApplyCustomBgFilter(keepSelection: true);
        _backgroundList.SelectionChanged += (_, _) => OnBuiltinBgChanged();
        _customBgList.SelectionChanged += (_, _) => OnCustomBgChanged();
        _customBgBrowseBtn.Click += (_, _) => BrowseCustomBgFolder();
        _exportCurrentBtn.Click += (_, _) => ExportCurrent();
        _exportAllBtn.Click += (_, _) => ExportAll();
        _themeToggleBtn.Click += (_, _) => ToggleTheme();
        _toolBgBrowseBtn.Click += (_, _) => BrowseToolBackground();
        _toolBgClearBtn.Click += (_, _) => ClearToolBackground();

        Opened += (_, _) => Dispatcher.UIThread.Post(LoadData, DispatcherPriority.Background);
    }

    // ─── Fluent-style helpers ────────────────────────────────────────

    private static IBrush Brush(byte r, byte g, byte b) =>
        new SolidColorBrush(Avalonia.Media.Color.FromRgb(r, g, b));

    private static IBrush Brush(byte a, byte r, byte g, byte b) =>
        new SolidColorBrush(Avalonia.Media.Color.FromArgb(a, r, g, b));

    private ListBox CreateListBox(double maxHeight) => new()
    {
        BorderBrush = BorderLineBrush,
        BorderThickness = new Thickness(1),
        Background = SurfaceAltBrush,
        Foreground = TextBrush,
        MaxHeight = maxHeight,
        Padding = new Thickness(2),
        ClipToBounds = true,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        ItemTemplate = CreateListItemTemplate()
    };

    private IDataTemplate CreateListItemTemplate() =>
        new FuncDataTemplate<object>((item, _) => new TextBlock
        {
            Text = item?.ToString() ?? string.Empty,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });

    private TextBox CreateSearchBox(string watermark) => new()
    {
        Watermark = watermark,
        Margin = new Thickness(0, 0, 0, 7),
        Padding = new Thickness(8, 4),
        Background = SurfaceAltBrush,
        Foreground = TextBrush,
        BorderBrush = BorderLineBrush,
        BorderThickness = new Thickness(1)
    };

    private Border CreateCard(Control content, Thickness? padding = null)
    {
        var card = new Border
        {
            Background = SurfaceBrush,
            BorderBrush = BorderLineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = padding ?? new Thickness(10),
            ClipToBounds = true,
            Child = content
        };
        _surfaceCards.Add(card);
        return card;
    }

    private TextBlock CreateCaption(string text)
    {
        var caption = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = MutedTextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        _captionTexts.Add(caption);
        return caption;
    }

    private TextBlock CreateTitle(string text, double size = 16)
    {
        var title = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush
        };
        _titleTexts.Add(title);
        return title;
    }

    // ─── Collapsible section builder ─────────────────────────────────

    private Control BuildCollapsibleSection(string label, Control content, bool startExpanded)
    {
        var arrow = new TextBlock
        {
            Text = startExpanded ? "▼ " : "▶ ",
            FontWeight = FontWeight.Bold,
            Foreground = MutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        _captionTexts.Add(arrow);
        var title = new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        _titleTexts.Add(title);
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { arrow, title },
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        content.IsVisible = startExpanded;

        header.PointerPressed += (_, _) =>
        {
            content.IsVisible = !content.IsVisible;
            arrow.Text = content.IsVisible ? "▼ " : "▶ ";
        };

        return CreateCard(new StackPanel
        {
            Children = { header, content }
        }, new Thickness(9));
    }

    // ─── Layout ──────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(22, 18, 22, 8)
        };
        var titleBlock = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                CreateTitle("Kaguya SP Viewer", 22),
                CreateCaption(_picDir)
            }
        };
        var canvasBadge = new Border
        {
            Background = SurfaceAltBrush,
            BorderBrush = BorderLineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"{_canvasWidth} x {_canvasHeight}",
                Foreground = MutedTextBrush,
                FontWeight = FontWeight.SemiBold
            }
        };
        _surfaceAltCards.Add(canvasBadge);
        if (canvasBadge.Child is TextBlock canvasText)
            _captionTexts.Add(canvasText);
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _themeToggleBtn, _toolBgBrowseBtn, _toolBgClearBtn, canvasBadge }
        };
        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(titleBlock);
        header.Children.Add(headerActions);

        var customBgToolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 8),
            Children = { _customBgBrowseBtn, _customBgPathText }
        };
        var backgroundContent = new StackPanel { Children = { _backgroundSearchBox, _backgroundList } };
        var customBgContent = new StackPanel { Children = { customBgToolbar, _customBgSearchBox, _customBgList } };
        var variantContent = new StackPanel { Children = { _variantSearchBox, _expressionList } };

        var leftPanel = new StackPanel
        {
            Width = 372,
            Spacing = 8,
            Children =
            {
                CreateCard(new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        CreateTitle("Workspace"),
                        CreateCaption("Select character, layers, and background.")
                    }
                }, new Thickness(10)),
                BuildCollapsibleSection("Character", _characterList, true),
                BuildCollapsibleSection("Variant", variantContent, true),
                BuildCollapsibleSection("Overlay", _overlayList, false),
                BuildCollapsibleSection("Background", backgroundContent, false),
                BuildCollapsibleSection("Custom BG", customBgContent, false)
            }
        };

        var leftScroll = new ScrollViewer
        {
            Width = 400,
            Content = leftPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 8, 10, 18)
        };

        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _exportCurrentBtn, _exportAllBtn }
        };
        var statusRow = new Border
        {
            Background = SurfaceAltBrush,
            BorderBrush = BorderLineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5),
            Child = _statusText
        };
        _surfaceAltCards.Add(statusRow);

        var progressRow = new Border
        {
            Background = SurfaceAltBrush,
            BorderBrush = BorderLineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Child = _progressBar
        };
        _surfaceAltCards.Add(progressRow);
        var bottomPanel = new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { actionButtons, statusRow, progressRow }
        };
        var previewBorder = new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderLineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4),
            Child = _previewImage
        };
        _panelCards.Add(previewBorder);
        var previewViewport = new AspectRatioBox((double)_canvasWidth / _canvasHeight)
        {
            Child = previewBorder
        };

        var rightPanel = new DockPanel
        {
            Margin = new Thickness(10, 8, 18, 18)
        };
        DockPanel.SetDock(bottomPanel, Dock.Bottom);
        rightPanel.Children.Add(bottomPanel);
        rightPanel.Children.Add(previewViewport);

        var mainGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(rightPanel, 1);
        mainGrid.Children.Add(leftScroll);
        mainGrid.Children.Add(rightPanel);

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(mainGrid, 1);
        content.Children.Add(header);
        content.Children.Add(mainGrid);

        return BuildRootBackground(content);
    }

    private Control BuildRootBackground(Control content)
    {
        var root = new Grid { Background = WindowBrush };
        _rootLayer = root;
        _toolBackgroundImage = new Avalonia.Controls.Image
        {
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false
        };
        _toolBackgroundOverlay = new Border { IsHitTestVisible = false };

        root.Children.Add(_toolBackgroundImage);
        root.Children.Add(_toolBackgroundOverlay);
        root.Children.Add(content);
        UpdateRootBackground();
        return root;
    }

    // ─── Data loading ────────────────────────────────────────────────

    private void LoadData()
    {
        _statusText.Text = "Building asset index...";
        _progressBar.IsIndeterminate = true;
        _progressBar.IsVisible = true;

        Task.Run(() =>
        {
            try
            {
                Dispatcher.UIThread.Post(() => _statusText.Text = "Scanning static assets...");
                var staticAssets = CharacterComposer.BuildStaticAssetIndex(_picDir);

                Dispatcher.UIThread.Post(() => _statusText.Text = "Scanning animated assets...");
                var animatedAssets = CharacterComposer.BuildAnimatedAssetIndex(_picDir);

                var characters = new List<SpCharacterGroup>();
                var overlays = new List<SpExpressionEntry>();

                Dispatcher.UIThread.Post(() => _statusText.Text = "Scanning backgrounds...");
                var backgrounds = BuildBackgroundList();

                if (_source.ParamsDocument?.Pattern is not null)
                {
                    Dispatcher.UIThread.Post(() => _statusText.Text = "Building Params SP plans...");
                    var usedStatic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var usedAnimated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var result = new CharacterComposeResult();

                    var plans = CharacterComposer.BuildSpPlans(
                        _source.ParamsDocument.Pattern, staticAssets, animatedAssets,
                        usedStatic, usedAnimated, result).ToArray();

                    (characters, overlays) = GroupPlansByCharacter(plans);
                }
                else if (!string.IsNullOrWhiteSpace(_source.TblstrScrDirectory) && Directory.Exists(_source.TblstrScrDirectory))
                {
                    Dispatcher.UIThread.Post(() => _statusText.Text = "Building TBLSTR SP plans...");
                    var usedStatic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var usedAnimated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var result = new CharacterComposeResult();

                    var plans = TblstrSpPlanBuilder.BuildPlansFromScrDirectory(
                        _source.TblstrScrDirectory,
                        staticAssets,
                        animatedAssets,
                        usedStatic,
                        usedAnimated,
                        result);

                    (characters, overlays) = GroupPlansByCharacter(plans);
                }

                var savedCustomBgFolder = LoadCustomBgFolder();
                var customBgs = new List<SpBackgroundEntry>();
                if (savedCustomBgFolder is not null && Directory.Exists(savedCustomBgFolder))
                {
                    customBgs = ScanCustomBgFolder(savedCustomBgFolder);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _characters = characters;
                    _overlays = overlays;
                    _backgrounds = backgrounds;
                    _customBgs = customBgs;

                    _characterList.ItemsSource = _characters;
                    _overlayList.ItemsSource = _overlays;
                    ApplyBackgroundFilter();
                    ApplyCustomBgFilter();

                    if (savedCustomBgFolder is not null)
                        _customBgPathText.Text = savedCustomBgFolder;

                    if (_characters.Count > 0)
                        _characterList.SelectedIndex = 0;
                    if (_backgrounds.Count > 0)
                        _backgroundList.SelectedIndex = 0;

                    _progressBar.IsIndeterminate = false;
                    _progressBar.IsVisible = false;
                    _exportCurrentBtn.IsEnabled = true;
                    _exportAllBtn.IsEnabled = true;

                    var totalExpr = _characters.Sum(c => c.Expressions.Count);
                    _statusText.Text = $"Loaded {_characters.Count} characters ({totalExpr} variants), {_overlays.Count} overlays, {_backgrounds.Count} backgrounds, {_customBgs.Count} custom BG";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _progressBar.IsIndeterminate = false;
                    _progressBar.IsVisible = false;
                    _statusText.Text = $"Error: {ex.Message}";
                });
            }
        });
    }

    // ─── Selection handlers ──────────────────────────────────────────

    private void OnCharacterChanged()
    {
        if (_characterList.SelectedItem is not SpCharacterGroup group)
        {
            _expressionList.ItemsSource = null;
            _previewImage.Source = null;
            return;
        }

        ApplyExpressionFilter();
    }

    private void ApplyExpressionFilter(bool keepSelection = false)
    {
        if (_characterList.SelectedItem is not SpCharacterGroup group)
        {
            _expressionList.ItemsSource = null;
            return;
        }

        var query = _variantSearchBox.Text?.Trim();
        var source = group.Expressions.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            source = source.Where(entry => tokens.All(token => VariantMatches(entry, token)));
        }

        var filtered = source.ToList();
        var previous = keepSelection ? _expressionList.SelectedItem as SpExpressionEntry : null;
        _expressionList.ItemsSource = filtered;

        if (previous is not null && filtered.Contains(previous))
        {
            _expressionList.SelectedItem = previous;
        }
        else if (filtered.Count > 0)
        {
            _expressionList.SelectedIndex = 0;
        }
        else
        {
            _expressionList.SelectedIndex = -1;
            _previewImage.Source = null;
            if (!string.IsNullOrWhiteSpace(query))
                _statusText.Text = $"Variant search: 0/{group.Expressions.Count}";
        }
    }

    private static bool VariantMatches(SpExpressionEntry entry, string token)
    {
        var indexText = entry.Index.ToString("D4");
        return indexText.Contains(token, StringComparison.OrdinalIgnoreCase)
               || entry.ToString().Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyBackgroundFilter(bool keepSelection = false)
    {
        var previous = keepSelection ? _backgroundList.SelectedItem as SpBackgroundEntry : null;
        var filtered = FilterBackgrounds(_backgrounds, _backgroundSearchBox.Text, keepNone: true);
        _backgroundList.ItemsSource = filtered;

        if (previous is not null && filtered.Contains(previous))
            _backgroundList.SelectedItem = previous;
        else if (filtered.Count > 0)
            _backgroundList.SelectedIndex = 0;
        else
            _backgroundList.SelectedIndex = -1;
    }

    private void ApplyCustomBgFilter(bool keepSelection = false)
    {
        var previous = keepSelection ? _customBgList.SelectedItem as SpBackgroundEntry : null;
        var filtered = FilterBackgrounds(_customBgs, _customBgSearchBox.Text, keepNone: false);
        _customBgList.ItemsSource = filtered;

        if (previous is not null && filtered.Contains(previous))
            _customBgList.SelectedItem = previous;
        else
            _customBgList.SelectedIndex = -1;
    }

    private static List<SpBackgroundEntry> FilterBackgrounds(IEnumerable<SpBackgroundEntry> entries, string? query, bool keepNone)
    {
        var tokens = query?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return entries
            .Where(entry => (keepNone && string.IsNullOrEmpty(entry.PngPath)) ||
                            tokens.Length == 0 ||
                            tokens.All(token => BackgroundMatches(entry, token)))
            .ToList();
    }

    private static bool BackgroundMatches(SpBackgroundEntry entry, string token) =>
        entry.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(entry.PngPath).Contains(token, StringComparison.OrdinalIgnoreCase);

    private void OnBuiltinBgChanged()
    {
        if (_suppressBgSync) return;
        if (_backgroundList.SelectedItem is SpBackgroundEntry)
        {
            _suppressBgSync = true;
            _customBgList.SelectedIndex = -1;
            _suppressBgSync = false;
        }
        OnSelectionChanged();
    }

    private void OnCustomBgChanged()
    {
        if (_suppressBgSync) return;
        if (_customBgList.SelectedItem is SpBackgroundEntry)
        {
            _suppressBgSync = true;
            _backgroundList.SelectedIndex = -1;
            _suppressBgSync = false;
        }
        OnSelectionChanged();
    }

    private SpBackgroundEntry? GetSelectedBackground()
    {
        if (_customBgList.SelectedItem is SpBackgroundEntry custom && !string.IsNullOrEmpty(custom.PngPath))
            return custom;
        return _backgroundList.SelectedItem as SpBackgroundEntry;
    }

    private void OnSelectionChanged()
    {
        if (_isExporting) return;

        var expression = _expressionList.SelectedItem as SpExpressionEntry;
        var background = GetSelectedBackground();
        var selectedOverlays = _overlayList.SelectedItems?.Cast<SpExpressionEntry>().ToList()
                               ?? new List<SpExpressionEntry>();

        if (expression is null)
        {
            _previewImage.Source = null;
            return;
        }

        _statusText.Text = "Composing preview...";
        Task.Run(() =>
        {
            try
            {
                var bitmap = ComposePreview(expression, background, selectedOverlays);
                var avBitmap = ConvertToAvalonia(bitmap);
                bitmap.Dispose();
                Dispatcher.UIThread.Post(() =>
                {
                    _previewImage.Source = avBitmap;
                    if (!_isExporting)
                        _statusText.Text = $"{expression} | {_canvasWidth}x{_canvasHeight}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _statusText.Text = $"Compose error: {ex.Message}");
            }
        });
    }

    // ─── Compose ─────────────────────────────────────────────────────

    private SysBitmap ComposePreview(SpExpressionEntry expression, SpBackgroundEntry? background, IReadOnlyList<SpExpressionEntry>? overlays = null)
    {
        var canvas = new SysBitmap(_canvasWidth, _canvasHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(SysColor.Transparent);

            if (background is not null && !string.IsNullOrEmpty(background.PngPath) && File.Exists(background.PngPath))
            {
                using var bgImage = new SysBitmap(background.PngPath);
                if (bgImage.Width >= _canvasWidth && bgImage.Height >= _canvasHeight
                    && Math.Abs((double)bgImage.Width / bgImage.Height - (double)_canvasWidth / _canvasHeight) > 0.02)
                {
                    var srcX = (bgImage.Width - _canvasWidth) / 2;
                    var srcY = (bgImage.Height - _canvasHeight) / 2;
                    graphics.DrawImage(bgImage,
                        new Rectangle(0, 0, _canvasWidth, _canvasHeight),
                        new Rectangle(srcX, srcY, _canvasWidth, _canvasHeight),
                        GraphicsUnit.Pixel);
                }
                else
                {
                    graphics.DrawImage(bgImage, 0, 0, _canvasWidth, _canvasHeight);
                }
            }

            foreach (var layer in expression.Layers)
            {
                var imagePath = layer.GetFramePath(0);
                using var image = new SysBitmap(imagePath);
                graphics.DrawImage(image, layer.OffsetX, layer.OffsetY, image.Width, image.Height);
            }

            if (overlays is not null)
            {
                foreach (var overlay in overlays)
                {
                    foreach (var layer in overlay.Layers)
                    {
                        var imagePath = layer.GetFramePath(0);
                        using var image = new SysBitmap(imagePath);
                        graphics.DrawImage(image, layer.OffsetX, layer.OffsetY, image.Width, image.Height);
                    }
                }
            }
        }

        return canvas;
    }

    private static AvBitmap ConvertToAvalonia(SysBitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return new AvBitmap(ms);
    }

    // ─── Export ──────────────────────────────────────────────────────

    private void SetExporting(bool exporting)
    {
        _isExporting = exporting;
        _exportCurrentBtn.IsEnabled = !exporting;
        _exportAllBtn.IsEnabled = !exporting;
        _characterList.IsEnabled = !exporting;
        _expressionList.IsEnabled = !exporting;
        _variantSearchBox.IsEnabled = !exporting;
        _overlayList.IsEnabled = !exporting;
        _backgroundSearchBox.IsEnabled = !exporting;
        _backgroundList.IsEnabled = !exporting;
        _customBgSearchBox.IsEnabled = !exporting;
        _customBgList.IsEnabled = !exporting;
    }

    private void ExportCurrent()
    {
        var expression = _expressionList.SelectedItem as SpExpressionEntry;
        var background = GetSelectedBackground();
        var character = _characterList.SelectedItem as SpCharacterGroup;
        var selectedOverlays = _overlayList.SelectedItems?.Cast<SpExpressionEntry>().ToList();

        if (expression is null || character is null)
        {
            _statusText.Text = "No expression selected.";
            return;
        }

        SetExporting(true);
        _progressBar.IsIndeterminate = false;
        _progressBar.IsVisible = true;
        _progressBar.Maximum = 1;
        _progressBar.Value = 0;
        _statusText.Text = "Exporting...";

        Task.Run(() =>
        {
            try
            {
                var exportDir = Path.Combine(_picDir, "..", "character", "sp_export");
                Directory.CreateDirectory(exportDir);

                var bgName = (background is null || string.IsNullOrEmpty(background.PngPath)) ? "nobg" : Path.GetFileNameWithoutExtension(background.Name);
                var fileName = $"{character.Name}_{expression}_{bgName}.png";
                var destPath = Path.Combine(exportDir, SanitizeFileName(fileName));

                using var bitmap = ComposePreview(expression, background, selectedOverlays);
                bitmap.Save(destPath, ImageFormat.Png);

                Dispatcher.UIThread.Post(() =>
                {
                    _progressBar.Value = 1;
                    _progressBar.IsVisible = false;
                    _statusText.Text = $"Exported: {Path.GetFileName(destPath)}";
                    SetExporting(false);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _progressBar.IsVisible = false;
                    _statusText.Text = $"Export failed: {ex.Message}";
                    SetExporting(false);
                });
            }
        });
    }

    private void ExportAll()
    {
        var character = _characterList.SelectedItem as SpCharacterGroup;
        var background = GetSelectedBackground();

        if (character is null)
        {
            _statusText.Text = "No character selected.";
            return;
        }

        var total = character.Expressions.Count;
        if (total == 0)
        {
            _statusText.Text = "No expressions to export.";
            return;
        }

        SetExporting(true);
        _progressBar.IsIndeterminate = false;
        _progressBar.IsVisible = true;
        _progressBar.Maximum = total;
        _progressBar.Value = 0;
        _statusText.Text = $"Exporting {character.Name}: 0/{total}";

        var capturedChar = character;
        var capturedBg = background;
        Task.Run(() =>
        {
            try
            {
                var bgName = (capturedBg is null || string.IsNullOrEmpty(capturedBg.PngPath)) ? "nobg" : Path.GetFileNameWithoutExtension(capturedBg.Name);
                var exportDir = Path.Combine(_picDir, "..", "character", "sp_export", SanitizeFileName(capturedChar.Name));
                Directory.CreateDirectory(exportDir);

                for (var i = 0; i < total; i++)
                {
                    var expr = capturedChar.Expressions[i];
                    var fileName = $"{expr}_{bgName}.png";
                    var destPath = Path.Combine(exportDir, SanitizeFileName(fileName));

                    using var bitmap = ComposePreview(expr, capturedBg);
                    bitmap.Save(destPath, ImageFormat.Png);

                    var progress = i + 1;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _progressBar.Value = progress;
                        _statusText.Text = $"Exporting {capturedChar.Name}: {progress}/{total} ({100 * progress / total}%)";
                    });
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _progressBar.IsVisible = false;
                    _statusText.Text = $"Exported {total} images -> sp_export/{capturedChar.Name}/";
                    SetExporting(false);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _progressBar.IsVisible = false;
                    _statusText.Text = $"Export failed: {ex.Message}";
                    SetExporting(false);
                });
            }
        });
    }

    // ─── Background scanning ─────────────────────────────────────────

    private List<SpBackgroundEntry> BuildBackgroundList()
    {
        var list = new List<SpBackgroundEntry> { SpBackgroundEntry.None };

        var archiveBackgroundDirs = Directory.Exists(_picDir)
            ? Directory.GetDirectories(_picDir)
                .Where(dir => Path.GetFileName(dir).StartsWith("bg", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();
        var backgroundDirs = archiveBackgroundDirs
            .Concat(new[] { "bgd", "BG" }
                .Select(name => Path.Combine(_picDir, name))
                .Where(Directory.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);

        foreach (var bgdDir in backgroundDirs)
        {
            foreach (var formatDir in Directory.GetDirectories(bgdDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var pngDir = Path.Combine(formatDir, "png");
                if (!Directory.Exists(pngDir))
                    continue;

                foreach (var png in Directory.GetFiles(pngDir, "*.png", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(png);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        list.Add(new SpBackgroundEntry(name, png));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("ＢＧ"))
                    {
                        list.Add(new SpBackgroundEntry(name, png));
                    }
                }
            }
        }

        return list;
    }

    // ─── Custom BG ───────────────────────────────────────────────────

    private async void BrowseCustomBgFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Custom Background Folder",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        var folderPath = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

        _statusText.Text = "Scanning custom backgrounds...";
        var path = folderPath;

        await Task.Run(() =>
        {
            var items = ScanCustomBgFolder(path);
            SaveCustomBgFolder(path);

            Dispatcher.UIThread.Post(() =>
            {
                _customBgs = items;
                ApplyCustomBgFilter();
                _customBgPathText.Text = path;
                _statusText.Text = $"Custom BG: {items.Count} images from {Path.GetFileName(path)}";
            });
        });
    }

    private void ToggleTheme()
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyThemeVariant();
        UpdateChromeState();
        SaveUiConfig();
        RefreshTheme();
    }

    private async void BrowseToolBackground()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Tool Background Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"]
                }
            ]
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        _toolBackgroundPath = path;
        UpdateChromeState();
        SaveUiConfig();
        RefreshTheme();
        _statusText.Text = $"Tool BG: {Path.GetFileName(path)}";
    }

    private void ClearToolBackground()
    {
        _toolBackgroundPath = null;
        UpdateChromeState();
        SaveUiConfig();
        RefreshTheme();
        _statusText.Text = "Tool BG cleared.";
    }

    private void ApplyThemeVariant()
    {
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = _isDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }

        Background = WindowBrush;
    }

    private void UpdateChromeState()
    {
        _themeToggleBtn.Content = _isDarkTheme ? "Light" : "Dark";
        _toolBgClearBtn.IsVisible = HasToolBackground;
    }

    private void RefreshControlTheme()
    {
        foreach (var list in new[] { _characterList, _expressionList, _overlayList, _backgroundList, _customBgList })
        {
            list.BorderBrush = BorderLineBrush;
            list.Background = SurfaceAltBrush;
            list.Foreground = TextBrush;
        }

        foreach (var searchBox in new[] { _variantSearchBox, _backgroundSearchBox, _customBgSearchBox })
        {
            searchBox.BorderBrush = BorderLineBrush;
            searchBox.Background = SurfaceAltBrush;
            searchBox.Foreground = TextBrush;
        }

        foreach (var card in _surfaceCards)
        {
            card.Background = SurfaceBrush;
            card.BorderBrush = BorderLineBrush;
        }

        foreach (var card in _surfaceAltCards)
        {
            card.Background = SurfaceAltBrush;
            card.BorderBrush = BorderLineBrush;
        }

        foreach (var card in _panelCards)
        {
            card.Background = PanelBrush;
            card.BorderBrush = BorderLineBrush;
        }

        foreach (var text in _titleTexts)
            text.Foreground = TextBrush;

        foreach (var text in _captionTexts)
            text.Foreground = MutedTextBrush;

        _customBgPathText.Foreground = MutedTextBrush;
        _statusText.Foreground = MutedTextBrush;
    }

    private void RefreshTheme()
    {
        RefreshControlTheme();
        UpdateRootBackground();
    }

    private void UpdateRootBackground()
    {
        if (_rootLayer is not null)
        {
            _rootLayer.Background = WindowBrush;
        }

        if (_toolBackgroundOverlay is not null)
        {
            _toolBackgroundOverlay.Background = HasToolBackground
                ? (_isDarkTheme
                    ? new SolidColorBrush(Avalonia.Media.Color.FromArgb(150, 18, 18, 20))
                    : new SolidColorBrush(Avalonia.Media.Color.FromArgb(168, 246, 241, 229)))
                : AvBrushes.Transparent;
        }

        if (_toolBackgroundImage is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(_toolBackgroundPath) && File.Exists(_toolBackgroundPath))
        {
            try
            {
                _toolBackgroundImage.Source = new AvBitmap(_toolBackgroundPath);
                _toolBackgroundImage.Opacity = _isDarkTheme ? 0.42 : 0.34;
                _toolBackgroundImage.IsVisible = true;
                return;
            }
            catch
            {
                _toolBackgroundPath = null;
                UpdateChromeState();
            }
        }

        _toolBackgroundImage.Source = null;
        _toolBackgroundImage.IsVisible = false;
    }

    private List<SpBackgroundEntry> ScanCustomBgFolder(string folderPath)
    {
        var list = new List<SpBackgroundEntry>();
        var extensions = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" };
        var targetRatio = (double)_canvasWidth / _canvasHeight;

        foreach (var ext in extensions)
        {
            foreach (var file in Directory.GetFiles(folderPath, ext, SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var img = new SysBitmap(file);
                    var oversized = img.Width >= _canvasWidth && img.Height >= _canvasHeight;
                    var ratioMatch = Math.Abs((double)img.Width / img.Height - targetRatio) < 0.02;
                    if (oversized || ratioMatch)
                    {
                        list.Add(new SpBackgroundEntry(Path.GetFileNameWithoutExtension(file), file));
                    }
                }
                catch
                {
                    // skip unreadable images
                }
            }
        }

        return list;
    }

    // ─── Config persistence ──────────────────────────────────────────

    private string? LoadCustomBgFolder()
    {
        var root = LoadConfigObject();
        return GetString(root["SpViewer"]?["CustomBgFolder"]);
    }

    private void SaveCustomBgFolder(string folderPath)
    {
        SaveUiConfig(customBgFolder: folderPath);
    }

    private void LoadUiConfig()
    {
        var root = LoadConfigObject();
        _isDarkTheme = GetBool(root["Gui"]?["DarkTheme"])
            ?? _isDarkTheme;
        _toolBackgroundPath = GetString(root["Gui"]?["ToolBackgroundPath"]);
    }

    private void SaveUiConfig(string? customBgFolder = null)
    {
        try
        {
            var root = LoadConfigObject();
            var gui = GetOrCreateGroup(root, "Gui");
            var spViewer = GetOrCreateGroup(root, "SpViewer");

            gui["DarkTheme"] = _isDarkTheme;
            gui["ToolBackgroundPath"] = _toolBackgroundPath;
            spViewer["CustomBgFolder"] = customBgFolder
                ?? GetString(spViewer["CustomBgFolder"]);

            Directory.CreateDirectory(ConfigRoot);
            File.WriteAllText(ConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }

    private JsonObject LoadConfigObject()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject ?? [];
        }
        catch
        {
            // ignore invalid config and rebuild a clean one on save
        }

        return [];
    }

    private static JsonObject GetOrCreateGroup(JsonObject root, string name)
    {
        if (root[name] is JsonObject group)
        {
            return group;
        }

        group = [];
        root[name] = group;
        return group;
    }

    private static string? GetString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static bool? GetBool(JsonNode? node)
    {
        try
        {
            return node?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    // ─── Grouping ────────────────────────────────────────────────────

    private static string BuildLabel(IEnumerable<string> parts)
    {
        var arr = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return arr.Length == 0 ? "unknown" : string.Join("+", arr);
    }

    private static (List<SpCharacterGroup> Characters, List<SpExpressionEntry> Overlays) GroupPlansByCharacter(CharacterComposer.SpCompositionPlan[] plans)
    {
        var planEntries = plans.Select(p => (
            Plan: p,
            Entry: new SpExpressionEntry(p.Index, BuildLabel(p.LabelParts), p.Layers, p.RequiresFrames)
        )).ToArray();

        var rawPrefixes = new List<string>();
        foreach (var (plan, _) in planEntries)
        {
            if (plan.Layers.Count < 2) continue;
            var prefix = GetLongestCommonPrefix(plan.LabelParts);
            if (prefix.Length > 0)
                rawPrefixes.Add(prefix);
        }

        var sorted = rawPrefixes.Distinct().OrderBy(p => p.Length).ToList();
        var characterNames = new List<string>();
        foreach (var prefix in sorted)
        {
            if (!characterNames.Any(cn => prefix.StartsWith(cn)))
                characterNames.Add(prefix);
        }

        var groups = new Dictionary<string, List<SpExpressionEntry>>();
        var overlays = new List<SpExpressionEntry>();
        foreach (var (plan, entry) in planEntries)
        {
            var label = plan.LabelParts.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
            var charName = !string.IsNullOrWhiteSpace(plan.CharacterHint)
                ? plan.CharacterHint
                : characterNames.FirstOrDefault(cn => label.StartsWith(cn));
            if (charName is null)
            {
                overlays.Add(entry);
            }
            else
            {
                if (!groups.ContainsKey(charName))
                    groups[charName] = new List<SpExpressionEntry>();
                groups[charName].Add(entry);
            }
        }

        var characters = groups
            .Where(g => g.Value.Count > 0)
            .OrderBy(g => g.Key)
            .Select(g => new SpCharacterGroup(g.Key, g.Value.OrderBy(e => e.Index).ToList()))
            .ToList();

        return (characters, overlays.OrderBy(e => e.Index).ToList());
    }

    private static string GetLongestCommonPrefix(IReadOnlyList<string> parts)
    {
        var names = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (names.Length == 0) return "";
        if (names.Length == 1) return names[0];

        var prefix = names[0];
        for (var i = 1; i < names.Length; i++)
        {
            var maxLen = Math.Min(prefix.Length, names[i].Length);
            var commonLen = 0;
            while (commonLen < maxLen && prefix[commonLen] == names[i][commonLen])
                commonLen++;
            prefix = prefix[..commonLen];
            if (prefix.Length == 0) return "";
        }

        return prefix;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}

internal sealed class AspectRatioBox : Decorator
{
    private readonly double _aspectRatio;

    public AspectRatioBox(double aspectRatio)
    {
        _aspectRatio = aspectRatio > 0 && !double.IsInfinity(aspectRatio) && !double.IsNaN(aspectRatio)
            ? aspectRatio
            : 16.0 / 9.0;
    }

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        var size = Fit(availableSize);
        Child?.Measure(size);
        return size;
    }

    protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
    {
        var size = Fit(finalSize);
        var x = Math.Max(0, (finalSize.Width - size.Width) / 2);
        var y = Math.Max(0, (finalSize.Height - size.Height) / 2);
        Child?.Arrange(new Rect(x, y, size.Width, size.Height));
        return finalSize;
    }

    private Avalonia.Size Fit(Avalonia.Size bounds)
    {
        var width = bounds.Width;
        var height = bounds.Height;

        if (double.IsInfinity(width) && double.IsInfinity(height))
            return new Avalonia.Size(640, 640 / _aspectRatio);

        if (double.IsInfinity(width))
            return new Avalonia.Size(height * _aspectRatio, height);

        if (double.IsInfinity(height))
            return new Avalonia.Size(width, width / _aspectRatio);

        if (width <= 0 || height <= 0)
            return new Avalonia.Size(0, 0);

        var current = width / height;
        return current > _aspectRatio
            ? new Avalonia.Size(height * _aspectRatio, height)
            : new Avalonia.Size(width, width / _aspectRatio);
    }
}
