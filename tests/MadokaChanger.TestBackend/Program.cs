using System.Text.Json;
// Process/transport test double only. This executable never inspects or attaches to game processes.
var first=await Console.In.ReadLineAsync();
if(first?.StartsWith('{')==true)
{
    var rest=await Console.In.ReadToEndAsync();using var request=JsonDocument.Parse(first+"\n"+rest);
    Console.WriteLine("{\"ok\":true,\"result\":{\"wads\":1}}");return;
}
var folder=AppContext.BaseDirectory;
await File.WriteAllTextAsync(Path.Combine(folder,"host.pid"),Environment.ProcessId.ToString());
var mode=File.Exists(Path.Combine(folder,"test-mode.txt"))?File.ReadAllText(Path.Combine(folder,"test-mode.txt")):"normal";
for(var line=first;line is not null;line=await Console.In.ReadLineAsync())
{
    await File.AppendAllTextAsync(Path.Combine(folder,"commands.log"),line+"\n");
    if(line=="start scan")
    {
        if(mode=="error"){Console.WriteLine("error 0.001 incompatible backend fixture");return;}
        if(mode!="no-ack")Console.WriteLine("status 0.001 injecting scanning for fixture");
    }
    else if(line=="stop"){Console.WriteLine("ok 0.002 stopped");return;}
    else Console.WriteLine("ok 0.000 config set");
}
