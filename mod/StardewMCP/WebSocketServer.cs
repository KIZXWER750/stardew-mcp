using System;
using System.Collections.Generic;
using System.Text.Json;
using StardewModdingAPI;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace StardewMCP;

/// <summary>WebSocket server for communication with the MCP server.</summary>
public class WebSocketServer
{
    private readonly IMonitor _monitor;
    private readonly GameStateSerializer _stateSerializer;
    private readonly CommandExecutor _commandExecutor;
    private WebSocketSharp.Server.WebSocketServer? _server;
    private GameBridge? _currentBridge;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public WebSocketServer(IMonitor monitor, GameStateSerializer stateSerializer, CommandExecutor commandExecutor)
    {
        _monitor = monitor;
        _stateSerializer = stateSerializer;
        _commandExecutor = commandExecutor;
    }

    public void Start(int port)
    {
        try
        {
            _server = new WebSocketSharp.Server.WebSocketServer(System.Net.IPAddress.Loopback, port);
            _server.AddWebSocketService<GameBridge>("/game", () =>
            {
                var bridge = new GameBridge(_monitor, _stateSerializer, _commandExecutor);
                _currentBridge = bridge;
                return bridge;
            });

            _server.Start();
            _monitor.Log($"WebSocket server started on ws://localhost:{port}/game", StardewModdingAPI.LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to start WebSocket server: {ex.Message}", StardewModdingAPI.LogLevel.Error);
        }
    }

    public void Stop()
    {
        _server?.Stop();
        _monitor.Log("WebSocket server stopped", StardewModdingAPI.LogLevel.Info);
    }

    public void BroadcastState()
    {
        _currentBridge?.SendState();
    }
}

/// <summary>WebSocket behavior for game communication.</summary>
public class GameBridge : WebSocketBehavior
{
    private readonly IMonitor _monitor;
    private readonly GameStateSerializer _stateSerializer;
    private readonly CommandExecutor _commandExecutor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public GameBridge(IMonitor monitor, GameStateSerializer stateSerializer, CommandExecutor commandExecutor)
    {
        _monitor = monitor;
        _stateSerializer = stateSerializer;
        _commandExecutor = commandExecutor;
    }

    protected override void OnOpen()
    {
        _monitor.Log("Client connected to WebSocket", StardewModdingAPI.LogLevel.Info);
        SendState();
    }

    protected override void OnClose(CloseEventArgs e)
    {
        _monitor.Log($"Client disconnected: {e.Reason}", StardewModdingAPI.LogLevel.Info);
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        _monitor.Log($"Received: {e.Data}", StardewModdingAPI.LogLevel.Debug);

        try
        {
            var message = JsonSerializer.Deserialize<WebSocketMessage>(e.Data, JsonOptions);
            if (message == null)
            {
                SendError("Invalid message format");
                return;
            }

            HandleMessage(message);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Error processing message: {ex.Message}", StardewModdingAPI.LogLevel.Error);
            SendError(ex.Message);
        }
    }

    protected override void OnError(ErrorEventArgs e)
    {
        _monitor.Log($"WebSocket error: {e.Message}", StardewModdingAPI.LogLevel.Error);
    }

    private void HandleMessage(WebSocketMessage message)
    {
        switch (message.Type?.ToLower())
        {
            case "command":
                HandleCommand(message);
                break;

            case "get_state":
                SendState();
                break;

            case "ping":
                SendPong(message.Id);
                break;

            default:
                SendError($"Unknown message type: {message.Type}");
                break;
        }
    }

    private void HandleCommand(WebSocketMessage message)
    {
        var command = new GameCommand
        {
            Id = message.Id ?? Guid.NewGuid().ToString(),
            Action = message.Action ?? "",
            Params = message.Params ?? new Dictionary<string, object>(),
            OnComplete = response =>
            {
                SendResponse(response, !(message.Action ?? "").StartsWith("farm_", StringComparison.Ordinal));
            }
        };

        var queueResult = _commandExecutor.QueueCommand(command);
        // Initial acknowledgment is sent, actual result comes via OnComplete callback
    }

    public void SendState()
    {
        if (State != WebSocketState.Open)
            return;

        try
        {
            var stateJson = _stateSerializer.GetGameStateJson();
            var response = new WebSocketResponse
            {
                Type = "state",
                Success = true,
                Data = JsonSerializer.Deserialize<object>(stateJson)
            };
            Send(JsonSerializer.Serialize(response, JsonOptions));
        }
        catch (Exception ex)
        {
            _monitor.Log($"Error sending state: {ex.Message}", StardewModdingAPI.LogLevel.Error);
        }
    }

    // Command callbacks are invoked by the main-thread command executor.
    // Keep the result deliverable even if diagnostic state serialization fails.
    private object? CaptureResponseState()
    {
        try
        {
            if (!StardewModdingAPI.Context.IsWorldReady) return null;
            return _stateSerializer.GetGameState();
        }
        catch (Exception ex)
        {
            _monitor.Log($"Command state capture failed: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
            return null;
        }
    }

    private void SendResponse(CommandResponse response, bool includeState = true)
    {
        try
        {
            var wsResponse = new WebSocketResponse
            {
                Id = response.Id,
                Type = "response",
                Success = response.Success,
                Message = response.Message,
                Data = response.Data,
                State = includeState ? CaptureResponseState() : null
            };
            Send(JsonSerializer.Serialize(wsResponse, JsonOptions));
        }
        catch (Exception ex)
        {
            _monitor.Log($"Error sending response: {ex.Message}", StardewModdingAPI.LogLevel.Error);
        }
    }

    private void SendError(string message)
    {
        var response = new WebSocketResponse
        {
            Type = "error",
            Success = false,
            Message = message
        };
        Send(JsonSerializer.Serialize(response, JsonOptions));
    }

    private void SendPong(string? id)
    {
        var response = new WebSocketResponse
        {
            Id = id ?? "",
            Type = "pong",
            Success = true
        };
        Send(JsonSerializer.Serialize(response, JsonOptions));
    }
}

#region Message Classes

public class WebSocketMessage
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Action { get; set; }
    public Dictionary<string, object>? Params { get; set; }
}

public class WebSocketResponse
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Success { get; set; }
    public string? Message { get; set; }
    public object? Data { get; set; }
    public object? State { get; set; }
}

#endregion
