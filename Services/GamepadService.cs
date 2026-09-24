using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private string _activeControllerName = "No controller detected";
    private bool _hasConnectedController;

    private const short StickDeadzone = 16000;
    private DateTime _lastStickNavTime = DateTime.MinValue;
    private const int StickRepeatIntervalMs = 220;

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

            int numJoysticks = SDL.SDL_NumJoysticks();
            for (int i = 0; i < numJoysticks; i++)
            {
                if (SDL.SDL_IsGameController(i) == SDL.SDL_bool.SDL_TRUE)
                {
                    OpenController(i);
                }
            }

            while (_running)
            {
                while (SDL.SDL_PollEvent(out SDL.SDL_Event e) == 1)
                {
                    switch (e.type)
                    {
                        case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                            OpenController(e.cdevice.which);
                            break;

                        case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                            CloseController(e.cdevice.which);
                            break;

                        case SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
                            HandleButtonDown((SDL.SDL_GameControllerButton)e.cbutton.button);
                            break;

                        case SDL.SDL_EventType.SDL_CONTROLLERAXISMOTION:
                            HandleAxisMotion((SDL.SDL_GameControllerAxis)e.caxis.axis, e.caxis.axisValue);
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
            foreach (var kvp in _openControllers)
            {
                SDL.SDL_GameControllerClose(kvp.Value);
            }
            _openControllers.Clear();
            SDL.SDL_QuitSubSystem(SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_JOYSTICK);
        }
    }

    private void OpenController(int deviceIndex)
    {
        IntPtr controller = SDL.SDL_GameControllerOpen(deviceIndex);
        if (controller != IntPtr.Zero)
        {
            int instanceId = SDL.SDL_JoystickInstanceID(SDL.SDL_GameControllerGetJoystick(controller));
            _openControllers[instanceId] = controller;
            string name = SDL.SDL_GameControllerName(controller) ?? $"Controller #{deviceIndex + 1}";
            _activeControllerName = name;
            _hasConnectedController = true;
            Console.WriteLine($"[GamepadService] Connected: {name} (ID: {instanceId})");
            ControllerStateChanged?.Invoke(_activeControllerName, true);
        }
    }

    private void CloseController(int instanceId)
    {
        if (_openControllers.TryGetValue(instanceId, out IntPtr controller))
        {
            SDL.SDL_GameControllerClose(controller);
            _openControllers.Remove(instanceId);
            Console.WriteLine($"[GamepadService] Disconnected controller (ID: {instanceId})");

            if (_openControllers.Count == 0)
            {
                _activeControllerName = "No controller detected";
                _hasConnectedController = false;
                ControllerStateChanged?.Invoke(_activeControllerName, false);
            }
            else
            {
                var remaining = _openControllers.Values.GetEnumerator();
                if (remaining.MoveNext())
                {
                    _activeControllerName = SDL.SDL_GameControllerName(remaining.Current) ?? "Controller";
                    ControllerStateChanged?.Invoke(_activeControllerName, true);
                }
            }
        }
    }

    private void HandleButtonDown(SDL.SDL_GameControllerButton button)
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
                // Select button -> opens settings
                ActionTriggered?.Invoke(GamepadAction.OpenSettings);
                break;
        }
    }

    private void HandleAxisMotion(SDL.SDL_GameControllerAxis axis, short value)
    {
        if (axis == SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY)
        {
            if (Math.Abs(value) > StickDeadzone)
            {
                if ((DateTime.UtcNow - _lastStickNavTime).TotalMilliseconds >= StickRepeatIntervalMs)
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
