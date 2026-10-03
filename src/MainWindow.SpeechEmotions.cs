using System.Text.RegularExpressions;

namespace QuietGPT;

public partial class MainWindow
{
    internal sealed record SpeechSegment(string Text, string? Mood);
    private bool emojiExpressions = true, emojiSounds;
    private static readonly Dictionary<string,string> Smileys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["😊"]="happy",["🙂"]="happy",["☺"]="happy",["😀"]="happy",["😃"]="happy",["😄"]="happy",["😁"]="happy",["🥰"]="affectionate",["😍"]="affectionate",["💚"]="affectionate",["❤"]="affectionate",
        ["💛"]="affectionate",["💙"]="affectionate",["💜"]="affectionate",["🩷"]="affectionate",["🧡"]="affectionate",["🤍"]="affectionate",["💕"]="affectionate",["💖"]="affectionate",["💗"]="affectionate",["💞"]="affectionate",["💘"]="affectionate",["♥"]="affectionate",
        ["😏"]="playful",["😉"]="wink",["💋"]="kiss",["😘"]="kiss",["😚"]="kiss",["😙"]="kiss",
        ["😂"]="amused",["🤣"]="amused",["😆"]="amused",
        ["😔"]="sad",["😞"]="sad",["😢"]="sad",["😭"]="sad",["🙁"]="sad",["☹"]="sad",
        ["😮"]="surprised",["😯"]="surprised",["😲"]="surprised",["😳"]="surprised",
        ["😠"]="annoyed",["😡"]="annoyed",["😤"]="annoyed",["🤔"]="thoughtful",["😟"]="thoughtful",
        [":)"]="happy",[":-)"]="happy",[";)"]="wink",[";-)"]="wink",[":D"]="happy",[":-D"]="happy",["xD"]="amused",["<3"]="affectionate",
        [":("]="sad",[":-("]="sad",[":o"]="surprised",[":-o"]="surprised"
    };
    private static readonly string SmileyPattern = string.Join("|",Smileys.Keys.Where(s=>s.Any(c=>c>127)).OrderByDescending(s=>s.Length).Select(Regex.Escape))
        +"|(?<![\\w/])(?:"+string.Join("|",Smileys.Keys.Where(s=>s.All(c=>c<=127)).OrderByDescending(s=>s.Length).Select(Regex.Escape))+")(?!\\w)";
    private static readonly Regex SmileyCue = new(SmileyPattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex TrailingSmiley = new("([.!?])[ \\t]*("+SmileyPattern+")",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static string RemoveEmoji(string text) => Regex.Replace(SmileyCue.Replace(text," "),@"[\uD83C-\uD83E][\uDC00-\uDFFF]|[\u2600-\u27BF\uFE0E\uFE0F\u200D\u20E3]"," ");

    private static string? CueMood(MatchCollection cues)
    {
        if(cues.Count==0)return null;
        var moods=cues.Select(c=>Smileys[c.Value]).ToArray();
        var mood=moods[^1];
        // Keep a smirk or wink alongside a heart/kiss in the same sentence.
        if(mood!="wink"&&moods.Contains("wink"))mood+="+wink";
        if(moods[^1]!="playful"&&moods.Contains("playful"))mood+="+playful";
        return mood;
    }

    internal static List<SpeechSegment> SpeechSegments(string text, bool expressions = true, bool chuckle = false)
    {
        // Code and URLs cannot supply facial or vocal cues.
        text=Regex.Replace(text,@"https?://\S+","link");
        text=Regex.Replace(text,@"```[\s\S]*?```"," ");
        var inline=new Dictionary<string,string>();
        text=Regex.Replace(text,@"`([^`\r\n]+)`",m=>{var key="\uE100"+inline.Count+"\uE101";inline[key]=Regex.Replace(RemoveEmoji(m.Groups[1].Value),@"\[(chuckle|laugh|sigh|gasp)\]","$1",RegexOptions.IgnoreCase);return key;});
        bool explicitSound=Regex.IsMatch(text,@"\[(?:clear throat|sigh|shush|cough|groan|sniff|gasp|chuckle|laugh)\]",RegexOptions.IgnoreCase);
        // A smiley just after a sentence belongs to that sentence, not the next one.
        for(int i=0;i<8;i++){var moved=TrailingSmiley.Replace(text,m=>" "+m.Groups[2].Value+m.Groups[1].Value);if(moved==text)break;text=moved;}
        var segments=new List<SpeechSegment>();
        foreach(var chunk in SpeechChunks(text))
        {
            var cues=SmileyCue.Matches(chunk);string? mood=CueMood(cues);
            var spoken=RemoveEmoji(chunk);foreach(var item in inline)spoken=spoken.Replace(item.Key,item.Value);
            spoken=Regex.Replace(spoken,@"\s+"," ").Trim();
            // Removing a smiley before punctuation should not introduce an odd pause.
            spoken=Regex.Replace(spoken,@"\s+([,.!?;:])","$1");
            if(!Regex.IsMatch(spoken,@"[\p{L}\p{N}]"))
            {
                if(expressions&&mood!=null&&segments.Count>0)segments[^1]=segments[^1] with {Mood=mood};
                continue;
            }
            foreach(var part in SpeechChunks(spoken))segments.Add(new(part,expressions?mood:null));
        }
        if(chuckle&&!explicitSound)
        {
            int index=segments.FindIndex(s=>s.Mood=="amused");
            if(index>=0)segments[index]=segments[index] with {Text="[chuckle] "+segments[index].Text};
        }
        return segments;
    }

    private void SaveVoicePreferences()
    {
        try{System.IO.File.WriteAllText(System.IO.Path.Combine(dataRoot,"voice.json"),System.Text.Json.JsonSerializer.Serialize(new {engine=speechEngine,reference=PortablePaths.SaveReference(speechReference),emojiExpressions,emojiSounds,speed=speechSpeed,outputDevice=speechOutputDevice,outputLabel=speechOutputLabel}));}catch{}
    }
}
