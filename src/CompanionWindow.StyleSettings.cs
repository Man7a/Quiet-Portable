using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuietGPT;
public partial class CompanionWindow
{
    private async Task PopulateStyleTab(StackPanel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock{Text="Loading style controls…",Foreground=Brushes.WhiteSmoke});
        try
        {
            if(!RiggedAvatar.Ready)throw new InvalidOperationException("Show the animated companion first, then reopen settings.");
            var raw=await RiggedAvatar.InspectAsync("JSON.stringify(settingsPages.map(([name,page])=>({name,controls:[...page.querySelectorAll('select,input,button')].filter(e=>e.id).map(e=>{const label=e.closest('label')?.cloneNode(true);label?.querySelectorAll('input,select,output,small').forEach(x=>x.remove());return {id:e.id,type:e.tagName==='SELECT'?'select':e.tagName==='BUTTON'?'button':e.type,label:label?.textContent.trim()||e.textContent.trim()||e.id,value:e.value,checked:e.checked||false,min:e.min||'0',max:e.max||'1',step:e.step||'.05',options:e.tagName==='SELECT'?[...e.options].map(o=>({value:o.value,text:o.textContent})):[]};})})))");
            using var doc=JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw)??"[]");
            panel.Children.Clear();
            panel.Children.Add(new TextBlock{Text="Make her your own",FontSize=22,Margin=new Thickness(0,0,0,5)});
            panel.Children.Add(new TextBlock{Text="Changes save as you adjust them. Try a motion test without closing your controls.",Foreground=new SolidColorBrush(Color.FromRgb(171,196,181)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
            bool syncing=false;
            var refresh=new Dictionary<string,Action<JsonElement>>();
            async Task Change(string id,string value,bool check=false,bool click=false)
            {
                await RiggedAvatar.InspectAsync("(()=>{const e=document.getElementById("+JsonSerializer.Serialize(id)+");if(!e)return;if("+(click?"true":"false")+"){e.click();return;}e."+(check?"checked":"value")+"="+(check?value:JsonSerializer.Serialize(value))+";e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));})()");
            }
            // Reset actions can change multiple values. Synchronize existing controls,
            // rather than rebuilding the page and losing expanded sections/scroll.
            async Task RefreshValues()
            {
                var values=await RiggedAvatar.InspectAsync("JSON.stringify([...document.querySelectorAll('select,input')].filter(e=>e.id).map(e=>({id:e.id,value:e.value,checked:e.checked||false})))");
                using var snapshot=JsonDocument.Parse(JsonSerializer.Deserialize<string>(values)??"[]");
                syncing=true;
                try{foreach(var item in snapshot.RootElement.EnumerateArray())if(refresh.TryGetValue(item.GetProperty("id").GetString()!,out var apply))apply(item);}
                finally{syncing=false;}
            }
            var feedback=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(226,185,164)),TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
            foreach(var group in doc.RootElement.EnumerateArray())
            {
                var controls=new StackPanel{Margin=new Thickness(16,0,16,14)};
                var tests=new WrapPanel{Margin=new Thickness(0,0,0,6)};
                var sectionName=group.GetProperty("name").GetString();
                var section=new Expander{Header=sectionName switch {"Look"=>"Appearance & accessories","Life"=>"Life & expressions","Hair"=>"Hair motion & layers","Chest"=>"Chest motion","Voice"=>"Voice direction",_=>"Wind & particles"},IsExpanded=sectionName=="Look",Content=controls,Foreground=Brushes.WhiteSmoke};
                panel.Children.Add(new Border{Child=section,Background=new SolidColorBrush(Color.FromRgb(30,44,35)),BorderBrush=new SolidColorBrush(Color.FromRgb(57,79,65)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Margin=new Thickness(0,0,0,10)});
                controls.Children.Add(tests);
                foreach(var item in group.GetProperty("controls").EnumerateArray())
                {
                    var id=item.GetProperty("id").GetString()!;var label=item.GetProperty("label").GetString();var type=item.GetProperty("type").GetString();
                    if(type=="checkbox")
                    {
                        var box=new CheckBox{Content=label,Tag=id,IsChecked=item.GetProperty("checked").GetBoolean(),Margin=new Thickness(0,7,0,7)};
                        box.Click+=async(_,_)=>{try{if(!syncing)await Change(id,box.IsChecked==true?"true":"false",true);}catch{feedback.Text="Could not update this setting. Please try again.";feedback.Visibility=Visibility.Visible;}};
                        refresh[id]=v=>box.IsChecked=v.GetProperty("checked").GetBoolean();controls.Children.Add(box);
                    }
                    else if(type=="button")
                    {
                        var button=new Button{Content=label,Tag=id,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,4,8,4)};
                        button.Click+=async(_,_)=>
                        {
                            button.IsEnabled=false;
                            try{await Change(id,"",click:true);if(id is "clearAccessories" or "resetAtmosphere")await RefreshValues();feedback.Visibility=Visibility.Collapsed;}
                            catch{feedback.Text="Could not run this action. Please try again.";feedback.Visibility=Visibility.Visible;}
                            finally{button.IsEnabled=true;}
                        };
                        if(id is "rigBounce" or "rigDown" or "rigContact" or "rigHair")tests.Children.Add(button);else controls.Children.Add(button);
                    }
                    else if(type=="select")
                    {
                        controls.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,8,0,5)});
                        var combo=new ComboBox{Tag=id,SelectedValuePath="Tag"};
                        foreach(var option in item.GetProperty("options").EnumerateArray())combo.Items.Add(new ComboBoxItem{Content=option.GetProperty("text").GetString(),Tag=option.GetProperty("value").GetString()});
                        combo.SelectedValue=item.GetProperty("value").GetString();
                        combo.SelectionChanged+=async(_,_)=>{try{if(!syncing&&combo.SelectedValue is string value)await Change(id,value);}catch{feedback.Text="Could not update this setting. Please try again.";feedback.Visibility=Visibility.Visible;}};
                        refresh[id]=v=>combo.SelectedValue=v.GetProperty("value").GetString();controls.Children.Add(combo);
                    }
                    else if(type=="range")
                    {
                        double Number(string key)=>double.Parse(item.GetProperty(key).GetString()!,CultureInfo.InvariantCulture);
                        var title=new TextBlock{Text=label+" · "+item.GetProperty("value").GetString(),Margin=new Thickness(0,10,0,2)};controls.Children.Add(title);
                        var slider=new Slider{Tag=id,Minimum=Number("min"),Maximum=Number("max"),Value=Number("value"),TickFrequency=Number("step"),SmallChange=Number("step"),LargeChange=Number("step")*10,IsSnapToTickEnabled=true};
                        System.Windows.Automation.AutomationProperties.SetName(slider,label);
                        slider.ValueChanged+=async(_,_)=>{var value=slider.Value.ToString("0.##",CultureInfo.InvariantCulture);title.Text=label+" · "+value;try{if(!syncing)await Change(id,value);}catch{feedback.Text="Could not update this setting. Please try again.";feedback.Visibility=Visibility.Visible;}};
                        refresh[id]=v=>slider.Value=double.Parse(v.GetProperty("value").GetString()!,CultureInfo.InvariantCulture);controls.Children.Add(slider);
                    }
                }
                if(tests.Children.Count==0)tests.Visibility=Visibility.Collapsed;
            }
            panel.Children.Add(feedback);
        }
        catch(Exception ex){panel.Children.Clear();panel.Children.Add(new TextBlock{Text=ex.Message,TextWrapping=TextWrapping.Wrap});}
    }
}
