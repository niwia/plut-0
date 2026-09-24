using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SDL2;

namespace Pluto.Services;

public enum GamepadAction
{
    NavigateUp,
    NavigateDown,
    PageUp,
    PageDown,
    Confirm,           // A / Cross
    ManageGame,        // X / Square
    FocusSearch,       // Y / Triangle
    BackOrCancel,      // B / Circle
    SyncAll,           // Start / Menu
    OpenSettings       // Back / Select / View
}

public class GamepadService : IDisposable
{
    private Thread? _pollingThread;
    private bool _running;
    private readonly Dictionary<int, IntPtr> _openControllers = new();
    private readonly Dictionary<int, IntPtr> _openJoysticks = new();
    private string _activeControllerName = "No controller detected";
    private bool _hasConnectedController;

    private const short StickDeadzone = 14000;
    private DateTime _lastStickNavTime = DateTime.MinValue;
    private DateTime _lastHatNavTime = DateTime.MinValue;
    private const int NavRepeatIntervalMs = 200;

    public event Action<GamepadAction>? ActionTriggered;
    public event Action<string, bool>? ControllerStateChanged;

    public string ActiveControllerName => _activeControllerName;
    public bool HasConnectedController => _hasConnectedController;

    public void Start()
    {
        if (_running) return;

        _running = true;
        _pollingThread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "SDL2GamepadService"
        };
        _pollingThread.Start();
    }

    private void RunLoop()
    {
        try
        {
            if (SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_JOYSTICK) < 0)
            {
                Console.WriteLine($"[GamepadService] SDL_Init failed: {SDL.SDL_GetError()}");
                return;
            }

            // Load community / Steam gamecontrollerdb mappings if available
            LoadCommunityMappings();

            // Enable joystick and gamecontroller events
            SDL.SDL_JoystickEventState(SDL.SDL_ENABLE);
            SDL.SDL_GameControllerEventState(SDL.SDL_ENABLE);

            // Open all currently connected devices
            ScanAndOpenDevices();

            while (_running)
            {
                while (SDL.SDL_PollEvent(out SDL.SDL_Event e) == 1)
                {
                    switch (e.type)
                    {
                        case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                        case SDL.SDL_EventType.SDL_JOYDEVICEADDED:
                            ScanAndOpenDevices();
                            break;

                        case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                        case SDL.SDL_EventType.SDL_JOYDEVICEREMOVED:
                            ScanAndOpenDevices();
                            break;

                        // 1. Standard SDL GameController Button events
                        case SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
                            HandleControllerButtonDown((SDL.SDL_GameControllerButton)e.cbutton.button);
                            break;

                        // 2. Standard SDL GameController Axis motion
                        case SDL.SDL_EventType.SDL_CONTROLLERAXISMOTION:
                            HandleControllerAxisMotion((SDL.SDL_GameControllerAxis)e.caxis.axis, e.caxis.axisValue);
                            break;

                        // 3. Joystick Hat motion (D-Pad on many Linux pads is a Hat!)
                        case SDL.SDL_EventType.SDL_JOYHATMOTION:
                            HandleJoyHatMotion(e.jhat.hatValue);
                            break;

                        // 4. Joystick Button fallback (for devices without GameController mapping)
                        case SDL.SDL_EventType.SDL_JOYBUTTONDOWN:
                            HandleJoyButtonDown(e.jbutton.button);
                            break;

                        // 5. Joystick Axis fallback
                        case SDL.SDL_EventType.SDL_JOYAXISMOTION:
                            HandleJoyAxisMotion(e.jaxis.axis, e.jaxis.axisValue);
                            break;
                    }
                }

                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GamepadService] Loop error: {ex.Message}");
        }
        finally
        {
            CloseAllDevices();
            SDL.SDL_QuitSubSystem(SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_JOYSTICK);
        }
    }

    private void LoadCommunityMappings()
    {
        string[] searchPaths = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam", "steamapps", "common", "DeathRoadToCanada", "data", "gamecontrollerdb.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "flatpak", "app", "org.ppsspp.PPSSPP", "x86_64", "stable", "193bbe95656ed696c8e5a5e42831ee8017d53514e9e0e0acaa3e1235e22089d3", "files", "share", "ppsspp", "assets", "gamecontrollerdb.txt")
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    int loaded = 0;
                    foreach (var line in File.ReadLines(path))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                        if (SDL.SDL_GameControllerAddMapping(trimmed) >= 0)
                        {
                            loaded++;
                        }
                    }
                    Console.WriteLine($"[GamepadService] Loaded {loaded} controller mappings from {path}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GamepadService] Error loading controller db: {ex.Message}");
                }
            }
        }
    }

    private void ScanAndOpenDevices()
    {
        int numJoysticks = SDL.SDL_NumJoysticks();
        bool anyFound = false;
        string firstName = "";

        for (int i = 0; i < numJoysticks; i++)
        {
            if (SDL.SDL_IsGameController(i) == SDL.SDL_bool.SDL_TRUE)
            {
                IntPtr controller = SDL.SDL_GameControllerOpen(i);
                if (controller != IntPtr.Zero)
                {
                    IntPtr joy = SDL.SDL_GameControllerGetJoystick(controller);
                    int instanceId = SDL.SDL_JoystickInstanceID(joy);
                    _openControllers[instanceId] = controller;
                    string name = SDL.SDL_GameControllerName(controller) ?? $"Controller #{i + 1}";
                    if (string.IsNullOrEmpty(firstName)) firstName = name;
                    anyFound = true;
                }
            }
            else
            {
                // Fallback: Open as generic Joystick
                IntPtr joy = SDL.SDL_JoystickOpen(i);
                if (joy != IntPtr.Zero)
                {
                    int instanceId = SDL.SDL_JoystickInstanceID(joy);
                    _openJoysticks[instanceId] = joy;
                    string name = SDL.SDL_JoystickName(joy) ?? $"Joystick #{i + 1}";
                    if (string.IsNullOrEmpty(firstName)) firstName = name;
                    anyFound = true;
                }
            }
        }

        _hasConnectedController = anyFound;
        _activeControllerName = anyFound ? firstName : "No controller detected";
        ControllerStateChanged?.Invoke(_activeControllerName, _hasConnectedController);
    }

    private void CloseAllDevices()
    {
        foreach (var kvp in _openControllers)
        {
            SDL.SDL_GameControllerClose(kvp.Value);
        }
        _openControllers.Clear();

        foreach (var kvp in _openJoysticks)
        {
            SDL.SDL_JoystickClose(kvp.Value);
        }
        _openJoysticks.Clear();
    }

    private void HandleControllerButtonDown(SDL.SDL_GameControllerButton button)
    {
        switch (button)
        {
            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A:
                ActionTriggered?.Invoke(GamepadAction.Confirm);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B:
                ActionTriggered?.Invoke(GamepadAction.BackOrCancel);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X:
                ActionTriggered?.Invoke(GamepadAction.ManageGame);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y:
                ActionTriggered?.Invoke(GamepadAction.FocusSearch);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP:
                ActionTriggered?.Invoke(GamepadAction.NavigateUp);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN:
                ActionTriggered?.Invoke(GamepadAction.NavigateDown);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER:
                ActionTriggered?.Invoke(GamepadAction.PageUp);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER:
                ActionTriggered?.Invoke(GamepadAction.PageDown);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START:
                ActionTriggered?.Invoke(GamepadAction.SyncAll);
                break;

            case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK:
                ActionTriggered?.Invoke(GamepadAction.OpenSettings);
                break;
        }
    }

    private void HandleControllerAxisMotion(SDL.SDL_GameControllerAxis axis, short value)
    {
        if (axis == SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY)
        {
            ProcessVerticalAxis(value);
        }
    }

    private void HandleJoyHatMotion(byte hatValue)
    {
        if ((DateTime.UtcNow - _lastHatNavTime).TotalMilliseconds < NavRepeatIntervalMs)
            return;

        if ((hatValue & SDL.SDL_HAT_UP) != 0)
        {
            _lastHatNavTime = DateTime.UtcNow;
            ActionTriggered?.Invoke(GamepadAction.NavigateUp);
        }
        else if ((hatValue & SDL.SDL_HAT_DOWN) != 0)
        {
            _lastHatNavTime = DateTime.UtcNow;
            ActionTriggered?.Invoke(GamepadAction.NavigateDown);
        }
        else if ((hatValue & SDL.SDL_HAT_LEFT) != 0)
        {
            _lastHatNavTime = DateTime.UtcNow;
            ActionTriggered?.Invoke(GamepadAction.PageUp);
        }
        else if ((hatValue & SDL.SDL_HAT_RIGHT) != 0)
        {
            _lastHatNavTime = DateTime.UtcNow;
            ActionTriggered?.Invoke(GamepadAction.PageDown);
        }
    }

    private void HandleJoyButtonDown(byte button)
    {
        // Fallback button mapping for generic joysticks
        switch (button)
        {
            case 0:
                ActionTriggered?.Invoke(GamepadAction.Confirm);
                break;
            case 1:
                ActionTriggered?.Invoke(GamepadAction.BackOrCancel);
                break;
            case 2:
                ActionTriggered?.Invoke(GamepadAction.ManageGame);
                break;
            case 3:
                ActionTriggered?.Invoke(GamepadAction.FocusSearch);
                break;
            case 6:
            case 8:
                ActionTriggered?.Invoke(GamepadAction.OpenSettings);
                break;
            case 7:
            case 9:
                ActionTriggered?.Invoke(GamepadAction.SyncAll);
                break;
            case 11:
            case 13:
                ActionTriggered?.Invoke(GamepadAction.NavigateUp);
                break;
            case 12:
            case 14:
                ActionTriggered?.Invoke(GamepadAction.NavigateDown);
                break;
        }
    }

    private void HandleJoyAxisMotion(byte axis, short value)
    {
        // Axis 1 is typically Left Stick Y or D-Pad Y
        if (axis == 1 || axis == 5 || axis == 7)
        {
            ProcessVerticalAxis(value);
        }
    }

    private void ProcessVerticalAxis(short value)
    {
        if (Math.Abs(value) > StickDeadzone)
        {
            if ((DateTime.UtcNow - _lastStickNavTime).TotalMilliseconds >= NavRepeatIntervalMs)
            {
                _lastStickNavTime = DateTime.UtcNow;
                if (value < -StickDeadzone)
                {
                    ActionTriggered?.Invoke(GamepadAction.NavigateUp);
                }
                else if (value > StickDeadzone)
                {
                    ActionTriggered?.Invoke(GamepadAction.NavigateDown);
                }
            }
        }
    }

    public void Stop()
    {
        _running = false;
        if (_pollingThread != null && _pollingThread.IsAlive)
        {
            _pollingThread.Join(500);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
