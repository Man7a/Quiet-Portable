using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuietGPT;

internal static class PortablePaths
{
    internal static string Root => AppContext.BaseDirectory;
    internal static string Data => Environment.GetCommandLineArgs().Contains("--smoke-test")
        ? Path.Combine(Root,"test-results","profile-test") : Path.Combine(Root,"Data");
    internal static string VoiceRoot => Path.Combine(Root,"VoiceRuntime");
    internal static string DefaultVoice => Path.Combine(Root,"Voice","F_Quiet.wav");
    internal static string VoicePython
    {
        get
        {
            try
            {
                using var ready=JsonDocument.Parse(File.ReadAllText(Path.Combine(VoiceRoot,"ready.json")));
                var path=Path.GetFullPath(Path.Combine(VoiceRoot,ready.RootElement.GetProperty("python").GetString()!));
                if(path.StartsWith(VoiceRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return path;
            }
            catch { }
            return Path.Combine(VoiceRoot,"python","python.exe");
        }
    }
    internal static bool VoiceReady => File.Exists(Path.Combine(VoiceRoot,"ready.json")) && File.Exists(VoicePython)
        && Directory.Exists(Path.Combine(VoiceRoot,"packages","chatterbox"))
        && File.Exists(Path.Combine(VoiceRoot,"models","turbo","t3_turbo_v1.safetensors"));
    internal static string InstanceName(bool smoke) => "Local\\QuietPortable."+(smoke?"Test.":"")+
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).ToUpperInvariant())))[..24];
    internal static string ImportVoice(string source)
    {
        var folder=Path.Combine(Data,"Voices");Directory.CreateDirectory(folder);
        var target=Path.Combine(folder,Guid.NewGuid().ToString("N")+Path.GetExtension(source));File.Copy(source,target);return target;
    }
    internal static string SaveReference(string path) => path.Length>0&&string.Equals(Path.GetFullPath(path),DefaultVoice,StringComparison.OrdinalIgnoreCase)?"@bundled/F_Quiet.wav":
        path.Length>0 && Path.GetFullPath(path).StartsWith(Data+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)?Path.GetRelativePath(Data,path):path;
    internal static string LoadReference(string path) => path=="@bundled/F_Quiet.wav"?DefaultVoice:path.Length==0?"":Path.IsPathRooted(path)?path:Path.GetFullPath(Path.Combine(Data,path));
}
