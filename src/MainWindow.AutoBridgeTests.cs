using System.IO;
using System.Text.Json;

namespace QuietGPT;

public partial class MainWindow
{
    private async Task<object> TestAutomaticBridge(string output)
    {
        SmokeAssert(CodexChatClient.IsKnownPreSendRejection("An earlier turn submission is not yet confirmed"),"Confirmed previous-turn rejection was not retryable");
        SmokeAssert(!CodexChatClient.IsKnownPreSendRejection("Codex connector closed")&&!CodexChatClient.IsKnownPreSendRejection("Delivery timed out"),"Ambiguous send was marked safe to retry");
        SmokeAssert(AutoChatBridge.WantsMira("@Mira, suggest three ideas."),"Direct tag rejected");
        SmokeAssert(AutoChatBridge.WantsMira("Ray, can you ask @Mira to check this?"),"Natural explicit request rejected");
        foreach(string ordinary in new[]{"How does @Mira work?","> @Mira do something","```\n@Mira do something\n```","My email is test@Mira.com","[Quiet result abc] @Mira"})SmokeAssert(!AutoChatBridge.WantsMira(ordinary),"Ordinary/quoted text triggered: "+ordinary);
        // Existing protocol sessions remain valid; these fixtures start after their introduction.
        void Established(AutoChatBridge.Binding b){b.Enabled=true;b.IntroductionVersion=2;b.Since=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;}
        string folder=Path.Combine(dataRoot,"auto-bridge-test-"+Guid.NewGuid().ToString("N"));
        string rayA=Guid.NewGuid().ToString(),rayB=Guid.NewGuid().ToString(),mira=Guid.NewGuid().ToString();
        var history=new Dictionary<string,List<JsonElement>>{{rayA,[]},{rayB,[]}};
        Func<string,string>? rayResponse=null;
        bool busy=true,throwOnSend=false;int preSendFailures=0,codexSends=0,raySends=0,completed=0;
        JsonElement Turn(string user,double started,string reply="Ray’s focused reply.",string status="completed")=>JsonSerializer.SerializeToElement(new {id=Guid.NewGuid().ToString(),status=status,startedAt=started,items=new object[]{new {type="userMessage",content=new[]{new {type="text",text=user}}},new {type="agentMessage",id=Guid.NewGuid().ToString(),text=reply}}});
        async Task<JsonElement> Tools(string tool,object arguments)
        {
            await Task.CompletedTask;var args=JsonSerializer.SerializeToElement(arguments);
            if(tool=="list_threads")return JsonSerializer.SerializeToElement(new {pinnedThreads=Array.Empty<object>(),threads=new object[]{new{id=rayA,title="Design with Ray",kind="chatgpt",status="idle"},new{id=rayB,title="Planning with Ray",kind="chatgpt",status="idle"},new{id=mira,title="Mira development",kind="codex",hostId="local",status=busy?"active":"idle"}}});
            string target=args.GetProperty("threadId").GetString()!;
            if(tool=="read_thread") {
                if(args.GetProperty("turnLimit").GetInt32()>10||args.GetProperty("maxOutputCharsPerItem").GetInt32()>20000)throw new ArgumentOutOfRangeException("read_thread limits exceeded");
            }
            if(tool=="read_thread")return target==mira?JsonSerializer.SerializeToElement(new {thread=new{id=mira,status=new{type=busy?"active":"idle"}},turns=Array.Empty<object>()}):JsonSerializer.SerializeToElement(new {turns=history[target].AsEnumerable().Reverse().ToArray()});
            if(tool=="send_message_to_thread"){
                if(target==mira){if(preSendFailures-->0)throw new CodexChatClient.DispatchException(false,"Connector unavailable before sending",new IOException("Test pipe closed"));codexSends++;if(throwOnSend)throw new IOException("Simulated uncertain delivery");}
                else {raySends++;string prompt=args.GetProperty("prompt").GetString()!;history[target].Add(Turn(prompt,DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2,rayResponse?.Invoke(prompt)??"Ray’s focused reply."));}
                return JsonSerializer.SerializeToElement(new {threadId=target});
            }
            throw new InvalidOperationException("Unexpected tool");
        }
        using(var bridge=new AutoChatBridge(folder,_=>Task.FromResult(true),(job,id)=>{completed++;return Task.CompletedTask;},Tools)){
            await bridge.RefreshChats();
            bridge.Add(bridge.Chats.Single(c=>c.Id==rayA),bridge.Chats.Single(c=>c.Id==mira));
            bridge.Add(bridge.Chats.Single(c=>c.Id==rayB),bridge.Chats.Single(c=>c.Id==mira));
            SmokeAssert(bridge.Bindings.All(b=>!b.Enabled),"New pairing enabled itself");
            foreach(var b in bridge.Bindings)Established(b);
            history[rayA].Add(Turn("@Mira old request",DateTimeOffset.UtcNow.ToUnixTimeSeconds()-100));
            history[rayA].Add(Turn("Not finished yet",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+1,"@Mira, incomplete request",status:"inProgress"));
            history[rayA].Add(Turn("How does @Mira work?",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+1));
            history[rayA].Add(Turn("@Mira, this is the user's wording, not Ray's request.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+1));
            history[rayA].Add(Turn("Please suggest ideas only.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2,"I'll ask Mira.\n@Mira, suggest ideas only."));
            history[rayB].Add(Turn("Ray, ask @Mira for a second opinion.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2,"@Mira, please give a second opinion."));
            await bridge.Tick();SmokeAssert(bridge.Jobs.Count==2&&codexSends==0,"User wording, old messages, or busy task handling failed");
            busy=false;await bridge.Tick();SmokeAssert(codexSends==1&&bridge.Jobs.Count(j=>j.State=="sent")==1,"Same-task queue sent concurrently");
            var first=bridge.Jobs.First(j=>j.State=="sent");
            File.WriteAllText(bridge.ResultPath(first),JsonSerializer.Serialize(new{Status="completed",Text="Three ideas, with no code changes."}));
            await bridge.Tick();SmokeAssert(raySends==1&&completed==1&&first.State=="done","Result did not return to originating Ray");
            SmokeAssert(codexSends==2,"Second queued request did not proceed");
            await bridge.Tick();SmokeAssert(codexSends==2&&raySends==1&&bridge.Jobs.Count==2,"Polling duplicated a send or created a loop");
            foreach(var b in bridge.Bindings)bridge.SetEnabled(b,false);
            history[rayA].Add(Turn("@Mira disabled request",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3));await bridge.Tick();SmokeAssert(bridge.Jobs.Count==2,"Off pairing accepted a request");
            var panel=new AutoChatBridgeWindow(bridge){Owner=this};try{panel.Show();await Task.Delay(150);CaptureElement(panel,Path.Combine(output,"automatic-ray-mira.png"));}finally{panel.Close();}
        }
        string uncertainFolder=Path.Combine(folder,"uncertain");codexSends=0;throwOnSend=true;history[rayA].Clear();
        using(var bridge=new AutoChatBridge(uncertainFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await bridge.RefreshChats();bridge.Add(bridge.Chats.Single(c=>c.Id==rayA),bridge.Chats.Single(c=>c.Id==mira));Established(bridge.Bindings[0]);
            history[rayA].Add(Turn("Please test uncertain delivery",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3,"@Mira, test uncertain delivery"));await bridge.Tick();await bridge.Tick();
            SmokeAssert(codexSends==1&&bridge.Jobs.Single().State=="uncertain"&&bridge.Jobs.Single().Info.Contains("Simulated uncertain delivery"),"Uncertain send retried or hid its error");
        }
        using(var restored=new AutoChatBridge(uncertainFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await restored.Tick();SmokeAssert(codexSends==1&&restored.Status.Contains("needs attention"),"Restart repeated or hid an uncertain send");
            throwOnSend=false;restored.RetryChecked(restored.Jobs.Single());await restored.Tick();
            SmokeAssert(codexSends==2&&restored.Jobs.Single().State=="sent","Checked manual retry did not dispatch once");
        }
        string retryFolder=Path.Combine(folder,"pre-send-retry");codexSends=0;preSendFailures=1;history[rayA].Clear();
        using(var bridge=new AutoChatBridge(retryFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await bridge.RefreshChats();bridge.Add(bridge.Chats.Single(c=>c.Id==rayA),bridge.Chats.Single(c=>c.Id==mira));Established(bridge.Bindings[0]);
            history[rayA].Add(Turn("Please test reconnect",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3,"@Mira, test reconnect"));await bridge.Tick();
            SmokeAssert(codexSends==0&&bridge.Jobs.Single().State=="queued"&&bridge.Jobs.Single().Info.Contains("Connector unavailable"),"Pre-send failure became uncertain or lost its cause");
        }
        using(var restored=new AutoChatBridge(retryFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            SmokeAssert(restored.Jobs.Single().State=="queued"&&restored.Jobs.Single().RetryAfter>DateTimeOffset.UtcNow,"Safe retry was not saved across restart");
            restored.Jobs.Single().RetryAfter=DateTimeOffset.MinValue;await restored.Tick();
            SmokeAssert(codexSends==1&&restored.Jobs.Single().State=="sent","Safe retry failed to send exactly once");
        }
        foreach(string ordinary in new[]{"How does @Mira work?","> @Mira do something","```text\n@Mira example\n```","~~~\n@Mira example\n~~~","    @Mira indented code","@Miranda hello","@Mira@example.com","@Mira"})
            SmokeAssert(AutoChatBridge.AddressedMessage(ordinary)=="","Example or unrelated mention dispatched: "+ordinary);
        SmokeAssert(AutoChatBridge.AddressedMessage("I'll ask her.\n@Mira, compare these.\n\n```cs\nint n=1;\n```\nSecond paragraph.")=="compare these.\n\n```cs\nint n=1;\n```\nSecond paragraph.","Addressed body lost paragraphs/code or included preface");
        SmokeAssert(AutoChatBridge.AddressedMessage("```\n@Mira example\n```\n@Mira: actual request")=="actual request","Prose after fenced example was ignored");
        throwOnSend=false;codexSends=0;raySends=0;history[rayA].Clear();
        rayResponse=_=>"For you: I will refine this.\n@Mira, refine the idea. No file changes.";
        string collaborationFolder=Path.Combine(folder,"collaboration");
        using(var bridge=new AutoChatBridge(collaborationFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await bridge.RefreshChats();bridge.Add(bridge.Chats.Single(c=>c.Id==rayA),bridge.Chats.Single(c=>c.Id==mira));Established(bridge.Bindings[0]);
            history[rayA].Add(Turn("My project is Quiet. We only want ideas.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()-20));
            history[rayA].Add(Turn("Please compare ideas only.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3,"I'll ask Mira.\n@Mira, suggest an idea."));
            for(int round=0;round<5;round++){
                await bridge.Tick();var current=bridge.Jobs.Single(j=>j.State=="sent");
                SmokeAssert(!bridge.Prompt(current).Contains("compare ideas only")&&!bridge.Prompt(current).Contains("My project is Quiet")&&current.Discussion.Contains("My project is Quiet"),"Conversation leaked into Ray's follow-up");
                if(round==4)rayResponse=_=>"Here is the conclusion for you.";
                File.WriteAllText(bridge.ResultPath(current),JsonSerializer.Serialize(new{RequestId=current.Id,RayChatId=current.RayId,CodexTaskId=current.MiraId,Status="completed",Text="Refined suggestion."}));
                await bridge.Tick();
                SmokeAssert(current.State=="done"&&current.ReturnText=="Mira: Refined suggestion.","Plain return or identical-result correlation failed");
            }
            await bridge.Tick();SmokeAssert(codexSends==5&&bridge.Bindings[0].Enabled&&bridge.Jobs.All(j=>j.State=="done"),"Follow-up cap, duplicate, or auto-off remains");
            SmokeAssert(bridge.Jobs.Select(j=>j.ReturnTurnId).Distinct().Count()==5,"Identical results matched the same old reply");
        }
        using(var restored=new AutoChatBridge(collaborationFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){await restored.Tick();SmokeAssert(codexSends==5&&restored.Bindings[0].Enabled,"Restart replayed work or disabled connection");}
        history[rayA].Clear();codexSends=0;busy=true;
        using(var bridge=new AutoChatBridge(Path.Combine(folder,"stop"),_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await bridge.RefreshChats();bridge.Add(bridge.Chats.Single(c=>c.Id==rayA),bridge.Chats.Single(c=>c.Id==mira));Established(bridge.Bindings[0]);
            history[rayA].Add(Turn("Ideas only.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3,"@Mira, suggest ideas."));await bridge.Tick();
            history[rayA].Add(Turn("Ray, stop working with Mira.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+4));await bridge.Tick();
            SmokeAssert(codexSends==0&&!bridge.Bindings[0].Enabled&&bridge.Jobs.All(j=>j.Stopped),"Stop did not prevent queued dispatch");
        }
        busy=false;history[rayA].Clear();codexSends=0;raySends=0;
        rayResponse=_=>"@Mira, this must not override a missing approval.";
        string switchFolder=Path.Combine(folder,"introduction");
        using(var bridge=new AutoChatBridge(switchFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await bridge.RefreshChats();bridge.Add(bridge.Chats.Single(c=>c.Id==rayA),bridge.Chats.Single(c=>c.Id==mira));
            bridge.SetEnabled(bridge.Bindings[0],true);bridge.SetEnabled(bridge.Bindings[0],true);
            await bridge.Tick();await bridge.Tick();
            SmokeAssert(raySends==1&&codexSends==0&&bridge.Bindings[0].Enabled,"Introduction repeated or authorized old work");
            history[rayA].Add(Turn("Compare the two ideas, no changes.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+10,"@Mira, compare the ideas."));
            await bridge.Tick();var current=bridge.Jobs.Last();
            File.WriteAllText(bridge.ResultPath(current),JsonSerializer.Serialize(new{RequestId=current.Id,RayChatId=current.RayId,CodexTaskId=current.MiraId,Status="needs_input",Text="Which options?"}));
            await bridge.Tick();await bridge.Tick();
            SmokeAssert(codexSends==1&&bridge.Jobs.All(j=>j.State=="done"),"Ray overrode needs-input result");
            history[rayA].Add(Turn("Compare A and B, ideas only.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+12,"@Mira, compare A and B without edits."));
            await bridge.Tick();SmokeAssert(codexSends==2&&bridge.Jobs.Last().Scope.Contains("A and B"),"Normal user clarification did not resume work");
            bridge.SetEnabled(bridge.Bindings[0],false);
        }
        string migrationFolder=Path.Combine(folder,"migration");Directory.CreateDirectory(Path.Combine(migrationFolder,"ChatBridge"));
        var oldBinding=new AutoChatBridge.Binding{RayId=rayA,MiraId=mira,Enabled=true,IntroductionVersion=1,Since=1};
        var oldSetup=new AutoChatBridge.Job{BindingId=oldBinding.Id,RayId=rayA,MiraId=mira,Introduction=true,State="returned"};
        File.WriteAllText(Path.Combine(migrationFolder,"ChatBridge","state.json"),JsonSerializer.Serialize(new{Bindings=new[]{oldBinding},Jobs=new[]{oldSetup}}));
        history[rayA].Clear();history[rayA].Add(Turn("Old request",DateTimeOffset.UtcNow.ToUnixTimeSeconds()-10,"@Mira, do not replay this."));
        int beforeMigration=codexSends;
        using(var bridge=new AutoChatBridge(migrationFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await bridge.Tick();await bridge.Tick();
            SmokeAssert(codexSends==beforeMigration&&bridge.Bindings[0].IntroductionVersion==2&&bridge.Jobs.First().Stopped&&bridge.Jobs.Count==2,"Old introduction blocked upgrade or replayed old request");
        }
        // Reproduce the missed handoff: same completed turn ID, initially lacking its addressed tail.
        string changingFolder=Path.Combine(folder,"changing-reply");history[rayA].Clear();busy=false;throwOnSend=false;
        var changing=Turn("Ask Mira about usage, no code changes.",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+10,"I'll ask her.");
        string changingId=changing.GetProperty("id").GetString()!;
        history[rayA].Add(changing);int beforeChanging=codexSends;
        using(var bridge=new AutoChatBridge(changingFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await bridge.RefreshChats();bridge.Add(bridge.Chats.Single(c=>c.Id==rayA),bridge.Chats.Single(c=>c.Id==mira));Established(bridge.Bindings[0]);
            bridge.Bindings[0].Seen.Add(changingId); // Migrate the exact 2.7 state that caused the loss.
            await bridge.Tick();SmokeAssert(bridge.Jobs.Count==0,"Incomplete snapshot dispatched");
            var node=System.Text.Json.Nodes.JsonNode.Parse(changing.GetRawText())!;
            node["items"]![1]!["text"]="I'll ask her.\n@Mira, explain the usage briefly.";
            history[rayA][0]=JsonSerializer.SerializeToElement(node);
            await bridge.Tick();
            SmokeAssert(codexSends==beforeChanging+1&&bridge.Jobs.Single().SourceTurnId==changingId,"Updated completed reply was permanently ignored");
            node["items"]![1]!["text"]="@Mira, explain usage briefly. Extra sentence after dispatch.";
            history[rayA][0]=JsonSerializer.SerializeToElement(node);
            await bridge.Tick();SmokeAssert(codexSends==beforeChanging+1,"Updated text duplicated an already dispatched turn");
        }
        using(var bridge=new AutoChatBridge(changingFolder,_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            await bridge.Tick();SmokeAssert(codexSends==beforeChanging+1&&bridge.Jobs.Count==1,"Restart replayed a changed reply");
        }
        int shortPromptCharacters,minimalPromptCharacters,historyCharactersSaved;
        using(var bridge=new AutoChatBridge(Path.Combine(folder,"context-size"),_=>Task.FromResult(true),(_,_)=>Task.CompletedTask,Tools)){
            var sample=new AutoChatBridge.Job{RayId=rayA,MiraId=mira,CollaborationId="test",Scope="Background delivery test only.",UserUpdates="No application edits.",RayContext="Background-delivery test only. Please reply with exactly: OK",Discussion=new string('x',6000)};
            string small=bridge.Prompt(sample);shortPromptCharacters=small.Length;
            SmokeAssert(!small.Contains(sample.Scope)&&!small.Contains(sample.UserUpdates)&&!small.Contains(sample.Discussion)&&!small.Contains(mira)&&small.Contains(bridge.ResultPath(sample)),"Conversation or metadata leaked into Ray's handoff");
            SmokeAssert(!small.Contains("RequestId")&&!small.Contains("RayChatId")&&!small.Contains("CodexTaskId")&&!small.Contains("Quiet handoff from Ray"),"Transport IDs or old handoff boilerplate leaked into the model prompt");
            var minimal=new AutoChatBridge.Job{RayId=rayA,MiraId=mira,Text="Reply exactly: OK",RayContext="Reply exactly: OK"};
            string minimum=bridge.Prompt(minimal);
            minimalPromptCharacters=minimum.Length;
            var resultPath=bridge.ResultPath(minimal);
            // Judge message overhead independently of the portable folder's path length.
            SmokeAssert(minimum.Replace(resultPath,"<result-file>").Length<400&&minimum.Replace("\r\n","\n").StartsWith("Reply exactly: OK\n")&&minimum.Split("Reply exactly: OK").Length==2&&!minimum.Contains("User scope")&&!minimum.Contains(mira)&&minimum.Split(resultPath).Length==2,"Self-contained request was repeated or padded: "+minimum.Length+" characters: "+minimum);
            minimal.CollaborationId="test";minimal.Scope="lets try a test again babe";
            SmokeAssert(!bridge.Prompt(minimal).Contains(minimal.Scope),"An exact-reply test repeated irrelevant user scope");
            sample.RayContext="[context] "+sample.RayContext;
            string full=bridge.Prompt(sample);historyCharactersSaved=sample.Discussion.Length;
            SmokeAssert(full.StartsWith(sample.RayContext)&&!full.Contains(sample.Discussion),"Ray's literal message changed or old discussion leaked");
            sample.RayContext="A new self-contained follow-up.";sample.Round=2;
            SmokeAssert(bridge.Prompt(sample).StartsWith(sample.RayContext)&&!bridge.Prompt(sample).Contains(sample.Scope)&&!bridge.Prompt(sample).Contains(sample.UserUpdates),"Follow-up included the user's conversation");
        }
        return new{rayMessageOnly=true,shortPromptCharacters,minimalPromptCharacters,historyCharactersSaved,updatedCompletedReplyRecovered=true,changedReplyNotDuplicated=true,assistantAddress=true,prefaceExcluded=true,codePreserved=true,oldMessagesIgnored=true,multipleRayChats=true,sameTaskQueue=true,plainReturn=true,identicalRepliesCorrelated=true,noDuplicates=true,offPauses=true,uncertainDeliveryNotRetried=true,preSendFailureRetried=true,manualRecoveryChecked=true,noReplyCap=true,stopCommand=true,needsInputWaitsForUser=true,introductionDoesNotStartOldWork=true,connectionStaysOn=true};
    }
}
