using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace QuietGPT;

public sealed partial class RiggedAvatarView
{
    private readonly Dictionary<string,TaskCompletionSource<JsonElement>> outputRequests=new();
    private string? outputDiscoveryError;
    public event Action? AudioOutputsChanged;
    private const string LocalAvatarOrigin="https://avatar.quiet.local";
    private async Task EnableOutputDiscovery(CoreWebView2 core)
    {
        // Chromium exposes the complete speaker list only with media-device
        // discovery permission. No input stream is ever requested. Block capture
        // APIs before any local script runs; navigation and native permission
        // requests remain denied. The chat uses a different WebView profile.
        await core.AddScriptToExecuteOnDocumentCreatedAsync("(()=>{const deny=()=>Promise.reject(new DOMException('This avatar only plays audio.','NotAllowedError'));if(navigator.mediaDevices){Object.defineProperty(navigator.mediaDevices,'getUserMedia',{value:deny,writable:false,configurable:false});Object.defineProperty(navigator.mediaDevices,'getDisplayMedia',{value:deny,writable:false,configurable:false});}for(const key of ['getUserMedia','webkitGetUserMedia'])if(key in navigator)Object.defineProperty(navigator,key,{value:(_c,_ok,fail)=>fail?.(new DOMException('Capture is disabled.','NotAllowedError')),writable:false,configurable:false});})()");
        try{await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone,LocalAvatarOrigin,CoreWebView2PermissionState.Allow);}
        catch(Exception ex) when(ex is System.Runtime.InteropServices.COMException or NotSupportedException){outputDiscoveryError="Could not list speakers. Update Microsoft Edge WebView2 and reopen Quiet.";}
    }
    private void OutputMessage(JsonElement message)
    {
        var kind=message.GetProperty("kind").GetString();
        if(kind=="audio-output-devices-changed"){AudioOutputsChanged?.Invoke();return;}
        if(!message.TryGetProperty("id",out var id)||!outputRequests.TryGetValue(id.GetString()??"",out var pending))return;
        if(message.GetProperty("ok").GetBoolean())pending.TrySetResult(message.GetProperty("result").Clone());
        else pending.TrySetException(new InvalidOperationException(message.GetProperty("error").GetString()??"Could not use this output device."));
    }
    private async Task<JsonElement> OutputRequest(string action,string deviceId="",string label="",bool silent=false,CancellationToken token=default)
    {
        if(!Ready||disposed)throw new InvalidOperationException("Show the companion before selecting speakers.");
        string id=Guid.NewGuid().ToString("N");
        var pending=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        outputRequests[id]=pending;
        try{Post(new{type="audio-output-"+action,id,deviceId,label,silent});return await pending.Task.WaitAsync(TimeSpan.FromSeconds(8),token);}
        finally{outputRequests.Remove(id);}
    }
    public Task<JsonElement> GetAudioOutputs()=>outputDiscoveryError==null?OutputRequest("list"):Task.FromException<JsonElement>(new InvalidOperationException(outputDiscoveryError));
    public Task<JsonElement> SetAudioOutput(string deviceId,string label,CancellationToken token=default)=>OutputRequest("set",deviceId,label,token:token);
    public Task<JsonElement> TestAudioOutput(bool silent=false)=>OutputRequest("test",silent:silent);
}
