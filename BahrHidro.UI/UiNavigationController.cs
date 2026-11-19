using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BahrHidro.UI
{
    public static class UiNavigationController
    {
        private sealed class NavigationEntry
        {
            public TabControl? TabControl { get; set; }
            public int PreviousTabIndex { get; set; }
            public FrameworkElement? PreviousPanel { get; set; }
            public FrameworkElement? CurrentPanel { get; set; }
            public string Title { get; set; } = string.Empty;
        }

        private sealed class WindowNavState
        {
            public Stack<NavigationEntry> History { get; } = new();
            public Button? BackButton { get; set; }
            public Button? HomeButton { get; set; }
            public TextBlock? BreadcrumbText { get; set; }
            public bool IsRestoring { get; set; }
            public readonly Dictionary<TabControl, int> LastSelectedIndices = new();
            public readonly Dictionary<Grid, FrameworkElement> LastVisiblePanelByGrid = new();
        }

        private static bool _initialized;
        private static readonly Dictionary<Window, WindowNavState> WindowStates = new();
        private static readonly List<WeakReference<Window>> SecondaryWindows = new();

        static UiNavigationController()
        {
            Initialize();
        }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnAnyWindowLoaded),
                    true);



                EventManager.RegisterClassHandler(
                    typeof(TabControl),
                    Selector.SelectionChangedEvent,
                    new SelectionChangedEventHandler(OnAnyTabSelectionChanged),
                    true);
            }
            catch
            {
            }
        }

        private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window) return;

            string asmName = window.GetType().Assembly.GetName().Name ?? string.Empty;
            if (!asmName.Contains("BahrHidro", StringComparison.OrdinalIgnoreCase))
                return;

            var state = GetOrCreateState(window);
            if (!Equals(window.Tag, "BH_NavHeaderAttached")) window.Closed += OnAnyWindowClosed;
            ManageSecondaryWindowExclusivity(window);
            AttachNavigationHeaderIfNeeded(window, state);
            HookOverlappingPanels(window, state);
            HookKeyboardShortcuts(window, state);
            UpdateNavigationHeaderUi(window, state, GetInitialViewTitle(window));
        }

        private static void OnAnyWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not Window window) return;
            WindowStates.Remove(window);

            SecondaryWindows.RemoveAll(wr => !wr.TryGetTarget(out var w) || w == window);

            if (window.Owner != null && window.Owner.IsLoaded)
            {
                try
                {
                    if (window.Owner.WindowState == WindowState.Minimized)
                        window.Owner.WindowState = WindowState.Normal;
                    window.Owner.Activate();
                }
                catch { }
            }
        }

        private static void ManageSecondaryWindowExclusivity(Window currentWindow)
        {
            bool isMainWindow = currentWindow.GetType().Name.Contains("MainWindow", StringComparison.OrdinalIgnoreCase)
                                && currentWindow.Owner == null;
            if (isMainWindow) return;

            for (int i = SecondaryWindows.Count - 1; i >= 0; i--)
            {
                if (!SecondaryWindows[i].TryGetTarget(out var other) || other == null || !other.IsLoaded)
                {
                    SecondaryWindows.RemoveAt(i);
                    continue;
                }

                if (other != currentWindow && currentWindow.Owner != other)
                {
                    try { other.Close(); } catch { }
                    SecondaryWindows.RemoveAt(i);
                }
            }

            if (!SecondaryWindows.Any(wr => wr.TryGetTarget(out var w) && w == currentWindow))
                SecondaryWindows.Add(new WeakReference<Window>(currentWindow));
        }

        private static void AttachNavigationHeaderIfNeeded(Window window, WindowNavState state)
        {
            if (Equals(window.Tag, "BH_NavHeaderAttached")) return;
            if (window.Content is not UIElement originalContent) return;

            window.Tag = "BH_NavHeaderAttached";

            var headerBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var btnBack = CreateHeaderButton("← Voltar", "Voltar para a aba ou janela anterior (Alt + Seta Esquerda)");
            btnBack.IsEnabled = window.Owner != null;
            btnBack.Click += (_, _) => GoBack(window);
            Grid.SetColumn(btnBack, 0);

            var btnHome = CreateHeaderButton("Início", "Voltar para a tela inicial");
            btnHome.Margin = new Thickness(6, 0, 12, 0);
            btnHome.IsEnabled = false;
            btnHome.Click += (_, _) => GoHome(window);
            Grid.SetColumn(btnHome, 1);

            var lblBreadcrumb = new TextBlock
            {
                Text = GetInitialViewTitle(window),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0")),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(lblBreadcrumb, 2);

            barGrid.Children.Add(btnBack);
            barGrid.Children.Add(btnHome);
            barGrid.Children.Add(lblBreadcrumb);
            headerBorder.Child = barGrid;

            state.BackButton = btnBack;
            state.HomeButton = btnHome;
            state.BreadcrumbText = lblBreadcrumb;

            window.Content = null;
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(headerBorder, Dock.Top);
            dock.Children.Add(headerBorder);
            dock.Children.Add(originalContent);
            window.Content = dock;
        }

        private static Button CreateHeaderButton(string text, string tooltip)
        {
            return new Button
            {
                Content = text,
                ToolTip = tooltip,
                Height = 26,
                Padding = new Thickness(10, 2, 10, 2),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569")),
                BorderThickness = new Thickness(1),
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Cursor = Cursors.Hand
            };
        }

        private static void HookKeyboardShortcuts(Window window, WindowNavState state)
        {
            window.PreviewKeyDown += (_, e) =>
            {
                bool altLeft = (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Left) || e.Key == Key.BrowserBack;
                if (altLeft)
                {
                    if (CanGoBack(window, state))
                    {
                        GoBack(window);
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.Escape && window.Owner != null && state.History.Count == 0)
                {
                    try { window.Close(); e.Handled = true; } catch { }
                }
            };
        }

        private static void OnAnyTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not TabControl tc || !ReferenceEquals(e.OriginalSource, tc))
                return;

            var window = Window.GetWindow(tc);
            if (window == null) return;

            string asmName = window.GetType().Assembly.GetName().Name ?? string.Empty;
            if (!asmName.Contains("BahrHidro", StringComparison.OrdinalIgnoreCase))
                return;

            var state = GetOrCreateState(window);
            int newIndex = tc.SelectedIndex;
            if (newIndex < 0) return;

            // Garante exclusividade visual entre TabItems
            for (int i = 0; i < tc.Items.Count; i++)
            {
                if (tc.Items[i] is TabItem ti && ti.Content is UIElement contentEl)
                    contentEl.Visibility = (i == newIndex) ? Visibility.Visible : Visibility.Collapsed;
            }

            string currentTitle = GetTabTitle(tc, newIndex);
            if (!state.IsRestoring && state.LastSelectedIndices.TryGetValue(tc, out int prevIndex) && prevIndex >= 0 && prevIndex != newIndex)
            {
                state.History.Push(new NavigationEntry
                {
                    TabControl = tc,
                    PreviousTabIndex = prevIndex,
                    Title = currentTitle
                });
            }

            state.LastSelectedIndices[tc] = newIndex;
            UpdateNavigationHeaderUi(window, state, currentTitle);
        }

        private static void HookOverlappingPanels(DependencyObject root, WindowNavState state)
        {
            foreach (var grid in FindVisualChildren<Grid>(root))
            {
                var viewChildren = grid.Children
                    .OfType<FrameworkElement>()
                    .Where(IsSwitchableViewContainer)
                    .ToList();

                if (viewChildren.Count < 2) continue;

                // Agrupa painéis que ocupam a mesma célula (Row/Column) e podem sobrepor
                var cellGroups = viewChildren
                    .GroupBy(c => (Row: Grid.GetRow(c), Col: Grid.GetColumn(c)))
                    .Where(g => g.Count() >= 2);

                foreach (var group in cellGroups)
                {
                    var siblings = group.ToList();
                    var initiallyVisible = siblings.FirstOrDefault(s => s.Visibility == Visibility.Visible) ?? siblings[0];

                    // Colapsa quaisquer painéis extras que já nasceram sobrepostos
                    foreach (var sib in siblings)
                    {
                        if (!ReferenceEquals(sib, initiallyVisible) && sib.Visibility == Visibility.Visible)
                            sib.Visibility = Visibility.Collapsed;
                    }
                    state.LastVisiblePanelByGrid[grid] = initiallyVisible;

                    foreach (var panel in siblings)
                    {
                        panel.IsVisibleChanged += (_, args) =>
                        {
                            if (args.NewValue is not true) return;

                            var window = Window.GetWindow(panel);
                            if (window == null) return;

                            state.LastVisiblePanelByGrid.TryGetValue(grid, out var prevPanel);

                            // Oculta imediatamente todos os outros painéis irmãos para nunca sobrepor
                            foreach (var other in siblings)
                            {
                                if (!ReferenceEquals(other, panel) && other.Visibility != Visibility.Collapsed)
                                    other.Visibility = Visibility.Collapsed;
                            }

                            string title = GetElementFriendlyTitle(panel);
                            if (!state.IsRestoring && prevPanel != null && !ReferenceEquals(prevPanel, panel))
                            {
                                state.History.Push(new NavigationEntry
                                {
                                    PreviousPanel = prevPanel,
                                    CurrentPanel = panel,
                                    Title = title
                                });
                            }

                            state.LastVisiblePanelByGrid[grid] = panel;
                            UpdateNavigationHeaderUi(window, state, title);
                        };
                    }
                }
            }
        }

        public static void GoBack(Window window)
        {
            if (window == null || !WindowStates.TryGetValue(window, out var state))
                return;

            if (state.History.Count == 0)
            {
                if (window.Owner != null)
                {
                    try { window.Close(); } catch { }
                }
                return;
            }

            var entry = state.History.Pop();
            state.IsRestoring = true;
            try
            {
                if (entry.TabControl != null && entry.PreviousTabIndex >= 0 && entry.PreviousTabIndex < entry.TabControl.Items.Count)
                {
                    entry.TabControl.SelectedIndex = entry.PreviousTabIndex;
                    UpdateNavigationHeaderUi(window, state, GetTabTitle(entry.TabControl, entry.PreviousTabIndex));
                }
                else if (entry.PreviousPanel != null)
                {
                    if (entry.CurrentPanel != null)
                        entry.CurrentPanel.Visibility = Visibility.Collapsed;
                    entry.PreviousPanel.Visibility = Visibility.Visible;
                    UpdateNavigationHeaderUi(window, state, GetElementFriendlyTitle(entry.PreviousPanel));
                }
            }
            finally
            {
                state.IsRestoring = false;
            }
        }

        public static void GoHome(Window window)
        {
            if (window == null || !WindowStates.TryGetValue(window, out var state))
                return;

            while (state.History.Count > 0)
                GoBack(window);
        }

        private static bool CanGoBack(Window window, WindowNavState state)
        {
            return state.History.Count > 0 || window.Owner != null;
        }

        private static void UpdateNavigationHeaderUi(Window window, WindowNavState state, string currentTitle)
        {
            if (state.BackButton != null)
                state.BackButton.IsEnabled = CanGoBack(window, state);
            if (state.HomeButton != null)
                state.HomeButton.IsEnabled = state.History.Count > 0;
            if (state.BreadcrumbText != null && !string.IsNullOrWhiteSpace(currentTitle))
                state.BreadcrumbText.Text = currentTitle;
        }

        private static bool IsSwitchableViewContainer(FrameworkElement fe)
        {
            if (fe is HeaderedContentControl || fe is ButtonBase || fe is TextBlock || fe is TextBox)
                return false;

            if (fe is UserControl || fe is TabControl)
                return true;

            if (fe is Panel || fe is Border)
                return !string.IsNullOrWhiteSpace(fe.Name);

            return false;
        }

        private static string GetInitialViewTitle(Window window)
        {
            var firstTc = FindVisualChildren<TabControl>(window).FirstOrDefault();
            if (firstTc != null && firstTc.SelectedIndex >= 0)
                return GetTabTitle(firstTc, firstTc.SelectedIndex);

            return string.IsNullOrWhiteSpace(window.Title) ? "Bahr Hidro BIM" : window.Title;
        }

        private static string GetTabTitle(TabControl tc, int index)
        {
            if (index < 0 || index >= tc.Items.Count) return "Bahr Hidro";
            if (tc.Items[index] is TabItem ti)
            {
                if (ti.Header is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
                if (ti.Header is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) return tb.Text.Trim();
                if (!string.IsNullOrWhiteSpace(ti.Name)) return FormatIdentifier(ti.Name);
            }
            return $"Aba {index + 1}";
        }

        private static string GetElementFriendlyTitle(FrameworkElement fe)
        {
            if (fe.Tag is string tag && !string.IsNullOrWhiteSpace(tag) && !tag.StartsWith("BH_"))
                return tag;
            if (!string.IsNullOrWhiteSpace(fe.Name))
                return FormatIdentifier(fe.Name);
            return "Bahr Hidro";
        }

        private static string FormatIdentifier(string name)
        {
            string clean = name
                .Replace("Panel", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Painel", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Grid", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Tab", "", StringComparison.OrdinalIgnoreCase)
                .Replace("View", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_", " ")
                .Trim();
            return string.IsNullOrWhiteSpace(clean) ? name : clean;
        }

        private static WindowNavState GetOrCreateState(Window window)
        {
            if (!WindowStates.TryGetValue(window, out var state))
            {
                state = new WindowNavState();
                WindowStates[window] = state;
            }
            return state;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    yield return typed;
                foreach (var sub in FindVisualChildren<T>(child))
                    yield return sub;
            }
        }
    }
}
