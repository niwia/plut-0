using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pluto.Models;
using Pluto.Services;

namespace Pluto;

public partial class MainWindow : Window
{
    private readonly PluginLibraryService _libraryService;
    private readonly SlsSteamService _slsService;
    private readonly GamepadService _gamepadService;

    private List<PluginGame> _allGames = new();
    private ObservableCollection<PluginGame> _displayedGames = new();

    public MainWindow()
    {
        InitializeComponent();

        _libraryService = new PluginLibraryService();
        _slsService = new SlsSteamService();
        _gamepadService = new GamepadService();

        GamesListBox.ItemsSource = _displayedGames;

        // Auto-refresh when plugin_library.json changes on disk
        _libraryService.LibraryChanged += () =>
        {
            Dispatcher.UIThread.Post(async () => await ReloadLibraryAsync());
        };

        // Wire gamepad navigation and actions
        _gamepadService.ActionTriggered += action =>
        {
            Dispatcher.UIThread.Post(() => HandleGamepadAction(action));
        };

        Loaded += OnWindowLoaded;
        Closing += (_, _) =>
        {
            _gamepadService.Dispose();
            _libraryService.Dispose();
        };
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _gamepadService.Start();
        await ReloadLibraryAsync();
        GamesListBox.Focus();
    }

    private async Task ReloadLibraryAsync()
    {
        var games = await _libraryService.LoadGamesAsync();
        _allGames = games;
        ApplyFilter(SearchBox?.Text);
    }

    private void ApplyFilter(string? query)
    {
        var trimmed = query?.Trim();
        IEnumerable<PluginGame> filtered = _allGames;

        if (!string.IsNullOrEmpty(trimmed))
        {
            filtered = _allGames.Where(g =>
                g.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                g.AppId.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                g.Depots.Any(d => d.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) ||
                g.InstallDir.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        _displayedGames.Clear();
        foreach (var item in list)
        {
            _displayedGames.Add(item);
        }

        if (EmptyStateText != null)
        {
            EmptyStateText.IsVisible = list.Count == 0;
            if (list.Count == 0 && !string.IsNullOrEmpty(trimmed))
            {
                EmptyStateText.Text = $"No games matching \"{trimmed}\"";
            }
            else
            {
                EmptyStateText.Text = "No games found in plugin library";
            }
        }

        if (list.Count > 0 && GamesListBox != null)
        {
            GamesListBox.SelectedIndex = 0;
            if (GamesListBox.SelectedItem != null)
            {
                GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
            }
        }
    }

    private void HandleGamepadAction(GamepadAction action)
    {
        switch (action)
        {
            case GamepadAction.NavigateUp:
                NavigateList(-1);
                break;

            case GamepadAction.NavigateDown:
                NavigateList(1);
                break;

            case GamepadAction.PageUp:
                NavigateList(-5);
                break;

            case GamepadAction.PageDown:
                NavigateList(5);
                break;

            case GamepadAction.ConfirmLaunch:
                if (GamesListBox.SelectedItem is PluginGame selected)
                {
                    LaunchGame(selected);
                }
                break;

            case GamepadAction.FocusSearch:
            case GamepadAction.ManageGame:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;

            case GamepadAction.BackOrCancel:
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                }
                GamesListBox.Focus();
                break;

            case GamepadAction.SyncAll:
                SyncAllGames();
                break;
        }
    }

    private void NavigateList(int offset)
    {
        if (_displayedGames.Count == 0) return;

        int currentIndex = GamesListBox.SelectedIndex;
        int newIndex = currentIndex + offset;

        if (newIndex < 0) newIndex = 0;
        if (newIndex >= _displayedGames.Count) newIndex = _displayedGames.Count - 1;

        GamesListBox.SelectedIndex = newIndex;
        if (GamesListBox.SelectedItem != null)
        {
            GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
        }
    }

    private void LaunchGame(PluginGame game)
    {
        Console.WriteLine($"[Pluto] Launching: {game.Name} ({game.AppId})");
        _slsService.LaunchGame(game.AppId);
    }

    private async void SyncAllGames()
    {
        Console.WriteLine("[Pluto] Syncing games to SLS config.yaml...");
        foreach (var g in _allGames)
        {
            await _slsService.SyncGameToConfigAsync(g);
        }
        _slsService.NotifyReload();
    }

    // UI Event Handlers
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter(SearchBox.Text);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || e.Key == Key.Enter)
        {
            GamesListBox.Focus();
            if (GamesListBox.SelectedIndex < 0 && _displayedGames.Count > 0)
            {
                GamesListBox.SelectedIndex = 0;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = string.Empty;
            GamesListBox.Focus();
            e.Handled = true;
        }
    }

    private void OnGameListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GamesListBox.SelectedItem is PluginGame selected)
        {
            LaunchGame(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Y || e.Key == Key.OemQuestion)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnGameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (GamesListBox.SelectedItem is PluginGame selected)
        {
            LaunchGame(selected);
        }
    }

    private void OnGameSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GamesListBox.SelectedItem != null)
        {
            GamesListBox.ScrollIntoView(GamesListBox.SelectedItem);
        }
    }
}