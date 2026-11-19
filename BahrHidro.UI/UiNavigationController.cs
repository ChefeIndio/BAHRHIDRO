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
        private sealed class NavStep
        {
            public TabControl? TabControl { get; set; }
            public int TabIndex { get; set; }
            public FrameworkElement? View { get; set; }
        }

        private sealed class WindowStateInfo
        {
            public Stack<NavStep> History { get; } = new();
            public Dictionary<TabControl, int> CurrentTabIndex { get; } = new();
            public Dictionary<DependencyObject, FrameworkElement> CurrentView { get; } = new();
            public bool IsNavigatingBack { get; set; }
            public bool IsHooked { get; set; }
        }

        private static bool _initialized;
        private static readonly Dictionary<Window, WindowStateInfo> States = new();
        private static readonly List<WeakReference<Window>> OpenSecondaryWindows = new();

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
                    new RoutedEventHandler(OnWindowLoaded),
                    true);

                EventManager.RegisterClassHandler(
                    typeof(TabControl),
                    Selector.SelectionChangedEvent,
                    new SelectionChangedEventHandler(OnTabSelectionChanged),
                    true);
            }
            catch
            {
            }
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window) return;

            string asmName = window.GetType().Assembly.GetName().Name ?? string.Empty;
            if (!asmName.Contains("BahrHidro", StringComparison.OrdinalIgnoreCase))
                return;

            var state = GetState(window);
            if (state.IsHooked) return;
            state.IsHooked = true;

            window.Closed += OnWindowClosed;
            ManageSecondaryWindow(window);
            HookUserControlPages(window, state);

            window.PreviewKeyDown += (_, args) =>
            {
                bool backShortcut = (Keyboard.Modifiers == ModifierKeys.Alt && args.Key == Key.Left)
                                    || args.Key == Key.BrowserBack;
                if (backShortcut && GoBack(window))
                {
                    args.Handled = true;
                }
                else if (args.Key == Key.Escape && window.Owner != null && state.History.Count == 0)
                {
                    try { window.Close(); args.Handled = true; } catch { }
                }
            };
        }

        private static void OnWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not Window window) return;
            States.Remove(window);
            OpenSecondaryWindows.RemoveAll(wr => !wr.TryGetTarget(out var w) || w == window);

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

        private static void ManageSecondaryWindow(Window window)
        {
            bool isMain = window.GetType().Name.Contains("MainWindow", StringComparison.OrdinalIgnoreCase)
                          && window.Owner == null;
            if (isMain) return;

            for (int i = OpenSecondaryWindows.Count - 1; i >= 0; i--)
            {
                if (!OpenSecondaryWindows[i].TryGetTarget(out var other) || other == null || !other.IsLoaded)
                {
                    OpenSecondaryWindows.RemoveAt(i);
                    continue;
                }

                if (other != window && window.Owner != other)
                {
                    try { other.Close(); } catch { }
                    OpenSecondaryWindows.RemoveAt(i);
                }
            }

            if (!OpenSecondaryWindows.Any(wr => wr.TryGetTarget(out var w) && w == window))
                OpenSecondaryWindows.Add(new WeakReference<Window>(window));
        }

        private static void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not TabControl tc || !ReferenceEquals(e.OriginalSource, tc))
                return;

            var window = Window.GetWindow(tc);
            if (window == null) return;

            string asmName = window.GetType().Assembly.GetName().Name ?? string.Empty;
            if (!asmName.Contains("BahrHidro", StringComparison.OrdinalIgnoreCase))
                return;

            var state = GetState(window);
            int idx = tc.SelectedIndex;
            if (idx < 0) return;

            if (!state.IsNavigatingBack &&
                state.CurrentTabIndex.TryGetValue(tc, out int prevIdx) &&
                prevIdx >= 0 && prevIdx != idx)
            {
                state.History.Push(new NavStep { TabControl = tc, TabIndex = prevIdx });
            }

            state.CurrentTabIndex[tc] = idx;
        }

        private static void HookUserControlPages(DependencyObject root, WindowStateInfo state)
        {
            foreach (var panel in FindVisualChildren<Panel>(root))
            {
                var pages = panel.Children.OfType<UserControl>().ToList();
                if (pages.Count < 2) continue;

                var active = pages.FirstOrDefault(p => p.Visibility == Visibility.Visible) ?? pages[0];
                foreach (var p in pages)
                {
                    if (!ReferenceEquals(p, active) && p.Visibility == Visibility.Visible)
                        p.Visibility = Visibility.Collapsed;
                }
                state.CurrentView[panel] = active;

                foreach (var page in pages)
                {
                    page.IsVisibleChanged += (_, args) =>
                    {
                        if (args.NewValue is not true) return;
                        var win = Window.GetWindow(page);
                        if (win == null) return;

                        state.CurrentView.TryGetValue(panel, out var prevPage);
                        foreach (var other in pages)
                        {
                            if (!ReferenceEquals(other, page) && other.Visibility != Visibility.Collapsed)
                                other.Visibility = Visibility.Collapsed;
                        }

                        if (!state.IsNavigatingBack && prevPage != null && !ReferenceEquals(prevPage, page))
                        {
                            state.History.Push(new NavStep { View = prevPage });
                        }

                        state.CurrentView[panel] = page;
                    };
                }
            }
        }

        public static bool GoBack(Window window)
        {
            if (window == null || !States.TryGetValue(window, out var state))
                return false;

            if (state.History.Count == 0)
            {
                if (window.Owner != null)
                {
                    try { window.Close(); return true; } catch { }
                }
                return false;
            }

            var step = state.History.Pop();
            state.IsNavigatingBack = true;
            try
            {
                if (step.TabControl != null && step.TabIndex >= 0 && step.TabIndex < step.TabControl.Items.Count)
                {
                    step.TabControl.SelectedIndex = step.TabIndex;
                    return true;
                }
                if (step.View != null)
                {
                    step.View.Visibility = Visibility.Visible;
                    return true;
                }
            }
            finally
            {
                state.IsNavigatingBack = false;
            }

            return false;
        }

        private static WindowStateInfo GetState(Window window)
        {
            if (!States.TryGetValue(window, out var state))
            {
                state = new WindowStateInfo();
                States[window] = state;
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
