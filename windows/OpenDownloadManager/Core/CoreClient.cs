using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;

namespace OpenDownloadManager.Core;

/// <summary>
/// The Rust core (odm.dll), driven with JSON: commands in through <see cref="Call"/>, events out
/// through <see cref="EventReceived"/>, which is raised on the UI thread.
/// </summary>
public sealed partial class CoreClient : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EventCallback(IntPtr json, IntPtr user);

    [DllImport("odm.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr odm_start(byte[] configJson, EventCallback callback, IntPtr user, out IntPtr errorOut);

    [DllImport("odm.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr odm_call(IntPtr core, byte[] commandJson);

    [DllImport("odm.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void odm_free_string(IntPtr s);

    [DllImport("odm.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void odm_stop(IntPtr core);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private IntPtr _core;
    // Held for the life of the core: the native side calls it from its own threads.
    private readonly EventCallback _callback;
    private readonly DispatcherQueue _ui;
    private readonly object _gate = new();

    public event Action<JsonObject>? EventReceived;

    private CoreClient(DispatcherQueue ui)
    {
        _ui = ui;
        _callback = OnNativeEvent;
    }

    public static CoreClient Start(object config, DispatcherQueue ui)
    {
        var client = new CoreClient(ui);
        var bytes = Utf8Z(JsonSerializer.Serialize(config, Json));
        client._core = odm_start(bytes, client._callback, IntPtr.Zero, out var error);
        if (client._core == IntPtr.Zero)
        {
            var message = TakeString(error) ?? "The download engine did not start.";
            throw new InvalidOperationException(message);
        }
        return client;
    }

    /// <summary>Runs a command and returns its reply. Blocks; prefer <see cref="CallAsync"/> on the UI thread.</summary>
    public JsonObject Call(object command)
    {
        var bytes = Utf8Z(JsonSerializer.Serialize(command, Json));
        IntPtr reply;
        lock (_gate)
        {
            if (_core == IntPtr.Zero) return Error("The download engine has stopped.");
            reply = odm_call(_core, bytes);
        }
        var text = TakeString(reply);
        return (text is null ? null : JsonNode.Parse(text) as JsonObject) ?? Error("No reply from the engine.");
    }

    public Task<JsonObject> CallAsync(object command) => Task.Run(() => Call(command));

    private void OnNativeEvent(IntPtr json, IntPtr user)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8(json);
            if (text is null || JsonNode.Parse(text) is not JsonObject ev) return;
            _ui.TryEnqueue(() => EventReceived?.Invoke(ev));
        }
        catch
        {
            // An exception must never unwind into native code.
        }
    }

    public void Dispose()
    {
        IntPtr core;
        lock (_gate)
        {
            core = _core;
            _core = IntPtr.Zero;
        }
        if (core != IntPtr.Zero) odm_stop(core);
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static byte[] Utf8Z(string s)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(s) + 1];
        System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    private static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { odm_free_string(p); }
    }
}
