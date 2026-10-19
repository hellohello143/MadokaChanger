using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MadokaChanger.App.ViewModels;
using MadokaChanger.Core;

namespace MadokaChanger.App;
public partial class App : Application
{
    public static ServiceProvider Services { get; private set; }=null!;
    public static bool IsHeadlessVerification { get; set; }
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root=Environment.GetEnvironmentVariable("MADOKACHANGER_DATA_ROOT")??Environment.GetEnvironmentVariable("SOULGEM_DATA_ROOT");
        var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(string.IsNullOrWhiteSpace(root)?AppPaths.ResolveDefaultRoot():root).ToLowerInvariant())));
        instance=new Mutex(true,"Local\\SoulGem-"+identity,out var ownsInstance); // Retain the legacy lock for shared upgraded data.
        if(!ownsInstance){instance.Dispose();instance=null;MessageBox.Show("MadokaChanger is already running for this data folder.","MadokaChanger");Shutdown();return;}
        CreateServices(string.IsNullOrWhiteSpace(root)?null:root);
        DispatcherUnhandledException+=(sender,args)=> {Services.GetRequiredService<ILogger<App>>().LogError(args.Exception,"Unhandled UI error");MessageBox.Show(args.Exception.Message,"MadokaChanger",MessageBoxButton.OK,MessageBoxImage.Error);args.Handled=true;};
        var window=Services.GetRequiredService<MainWindow>();MainWindow=window;window.Show();
    }
    public static ServiceProvider CreateServices(string? root)
    {
        var paths=new AppPaths(root);
        var services=new ServiceCollection().AddSingleton(paths)
            .AddLogging(b=>b.ClearProviders().AddProvider(new JsonLogProvider(paths.Logs)))
            .AddSingleton(new HttpClient{Timeout=TimeSpan.FromMinutes(10)})
            .AddSingleton<RemoteClient>().AddSingleton<StateStore>().AddSingleton<LibraryStore>().AddSingleton<OperationGate>()
            .AddSingleton<MetadataProvider>().AddSingleton<ImageCache>().AddSingleton<IRepositorySource,GitHubRepository>()
            .AddSingleton<NativeBridge>().AddSingleton<PackageValidator>().AddSingleton<LibraryInstaller>()
            .AddSingleton<CustomSkinService>().AddSingleton<IGameEnvironment,SystemGameEnvironment>().AddSingleton<PatcherController>().AddSingleton<MainViewModel>().AddSingleton<MainWindow>();
        Services=services.BuildServiceProvider();
        return Services;
    }
    protected override void OnExit(ExitEventArgs e) {Services?.Dispose();if(instance is not null){instance.ReleaseMutex();instance.Dispose();}base.OnExit(e);}
}
internal sealed class JsonLogProvider(string directory) : ILoggerProvider
{
    private readonly object sync=new();
    public ILogger CreateLogger(string categoryName)=>new JsonLogger(this,categoryName);
    public void Dispose(){ }
    private void Write(string category,LogLevel level,string message,Exception? error)
    {
        lock(sync)
        {
            var path=Path.Combine(directory,$"madokachanger-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            if(File.Exists(path)&&new FileInfo(path).Length>20*1024*1024)File.Move(path,path+"."+DateTimeOffset.UtcNow.ToUnixTimeSeconds(),true);
            File.AppendAllText(path,JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,level=level.ToString(),category,message,exception=error?.ToString()})+Environment.NewLine);
        }
    }
    private sealed class JsonLogger(JsonLogProvider owner,string category):ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>level>=LogLevel.Information;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> formatter){if(IsEnabled(level))owner.Write(category,level,formatter(state,error),error);}
    }
}
