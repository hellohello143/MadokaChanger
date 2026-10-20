using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Windows;

internal static class Program
{
    private const string ExpectedOldCoreSha256 = "26F902B54749BF4E81DBD9EDCA1D80BF9CCE6DC75C3084BFFE23AF178006E7F7";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 2 or > 6)
        {
            Console.Error.WriteLine("MadokaChanger.UpdateVerify <old-publish-directory> <isolated-data-root> [expected-old-version] [expected-latest-version] [download|check-only] [expected-old-core-sha256]");
            return 2;
        }

        var oldPublish = Path.GetFullPath(args[0]);
        var dataRoot = Path.GetFullPath(args[1]);
        var expectedOld = args.Length >= 3 ? Version.Parse(args[2]) : new Version(1, 1, 0);
        var expectedLatest = args.Length >= 4 && args[3] != "latest" ? Version.Parse(args[3]) : null;
        var mode = args.Length >= 5 ? args[4] : "download";
        if (mode is not ("download" or "check-only")) throw new ArgumentException("Mode must be download or check-only.");
        var expectedCoreHash = args.Length >= 6 ? args[5] : null;
        var corePath = Path.Combine(oldPublish, "MadokaChanger.Core.dll");
        if (!File.Exists(corePath)) throw new FileNotFoundException("The saved old Core DLL was not found.", corePath);
        await using var coreStream = File.OpenRead(corePath);
        var oldHash = Convert.ToHexString(await SHA256.HashDataAsync(coreStream)).ToLowerInvariant();
        if (expectedOld == new Version(1, 1, 0) && !string.Equals(oldHash, ExpectedOldCoreSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The v1.1.0 Core DLL changed. Expected {ExpectedOldCoreSha256}, got {oldHash}.");
        if (expectedCoreHash is not null && !string.Equals(oldHash, expectedCoreHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The old Core DLL hash differs from the expected value. Expected {expectedCoreHash}, got {oldHash}.");

        Directory.CreateDirectory(dataRoot);
        var loadContext = new OldReleaseLoadContext(oldPublish);
        var core = loadContext.LoadFromAssemblyPath(corePath);
        var updateServiceType = RequireType(core, "MadokaChanger.Core.UpdateService");
        var currentVersion = (Version)(updateServiceType.GetProperty("CurrentVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException("UpdateService.CurrentVersion"));
        if (ReleaseNormalize(currentVersion) != ReleaseNormalize(expectedOld))
            throw new InvalidDataException($"The updater under test must be v{expectedOld}, but its Core reports {currentVersion}.");

        var paths = Activator.CreateInstance(RequireType(core, "MadokaChanger.Core.AppPaths"), dataRoot)!;
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            var remoteType = RequireType(core, "MadokaChanger.Core.RemoteClient");
            var nativeType = RequireType(core, "MadokaChanger.Core.NativeBridge");
            var remote = Activator.CreateInstance(remoteType, http, CreateNullLogger(loadContext, remoteType))!;
            var native = Activator.CreateInstance(nativeType, CreateNullLogger(loadContext, nativeType), null)!;
            var service = Activator.CreateInstance(updateServiceType, remote, paths, native, CreateNullLogger(loadContext, updateServiceType))!;

            var check = updateServiceType.GetMethod("CheckAsync", [typeof(CancellationToken)])
                ?? throw new MissingMethodException(updateServiceType.FullName, "CheckAsync");
            var report = await AwaitResultAsync(check.Invoke(service, [CancellationToken.None])!)
                ?? throw new InvalidDataException("The old updater returned no update report.");
            var appCurrent = (Version)(Get(report, "AppCurrent") ?? throw new InvalidDataException("The old updater returned no current version."));
            var latest = Get(report, "AppLatest") ?? throw new InvalidDataException("GitHub returned no latest MadokaChanger release.");
            var latestVersion = (Version)(Get(latest, "Version") ?? throw new InvalidDataException("The GitHub release has no parsed version."));
            var available = (bool)(Get(report, "AppUpdateAvailable") ?? false);
            if (expectedLatest is not null && latestVersion != expectedLatest) throw new InvalidDataException($"Expected GitHub latest v{expectedLatest}, got v{latestVersion}.");
            if (ReleaseNormalize(appCurrent) != ReleaseNormalize(expectedOld)) throw new InvalidDataException($"Unexpected old updater version {appCurrent}.");
            var expectedAvailable = ReleaseNormalize(latestVersion) > ReleaseNormalize(appCurrent);
            if (available != expectedAvailable) throw new InvalidDataException($"The v{appCurrent} updater reported AppUpdateAvailable={available}; expected {expectedAvailable} for v{latestVersion}.");
            if (mode == "download" && !available) throw new InvalidDataException($"The v{appCurrent} updater did not report v{latestVersion} as an available app update.");

            var setupAsset = GetAppSetupAsset(updateServiceType.GetMethod("AppSetupAsset", BindingFlags.Public | BindingFlags.Static)!, latest);
            var expectedAssetName = (string)(Get(setupAsset, "Name") ?? "");
            var digest = (string?)(Get(setupAsset, "Sha256"));
            if (!expectedAssetName.StartsWith("MadokaChanger-Setup-", StringComparison.OrdinalIgnoreCase) || digest is not { Length: 64 })
                throw new InvalidDataException("GitHub latest has no setup asset with a published SHA-256 digest.");

            string? setupPath = null;
            if (mode == "download")
            {
                var download = updateServiceType.GetMethod("DownloadAppUpdateAsync")
                    ?? throw new MissingMethodException(updateServiceType.FullName, "DownloadAppUpdateAsync");
                var downloadTask = download.Invoke(service, [latest, null, CancellationToken.None])!;
                setupPath = (string)(await AwaitResultAsync(downloadTask) ?? throw new InvalidDataException("The updater returned no setup path."));
                await using var setup = File.OpenRead(setupPath);
                var downloadedHash = Convert.ToHexString(await SHA256.HashDataAsync(setup)).ToLowerInvariant();
                if (!string.Equals(downloadedHash, digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The old updater's downloaded setup does not match GitHub's SHA-256 digest.");
            }

            var appAssembly = loadContext.LoadFromAssemblyPath(Path.Combine(oldPublish, "MadokaChanger.dll"));
            var buttonProof = VerifyOldUpdateCommand(appAssembly, core);
            var result = new
            {
                oldUpdater = currentVersion.ToString(3),
                oldCoreSha256 = oldHash,
                githubLatest = latestVersion.ToString(3),
                updateAvailable = available,
                asset = expectedAssetName,
                githubSha256 = digest,
                downloadedSetup = setupPath,
                downloadedSha256 = mode == "download" ? digest : null,
                mode,
                updateCommandReferences = buttonProof,
                updateCommandExecuted = false,
                appRestarted = false
            };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "update-verification.json"), json);
            Console.WriteLine(json);
            return 0;
        }
        finally { http.Dispose(); }
    }

    private static object GetAppSetupAsset(MethodInfo method, object latest)
    {
        var asset = method.Invoke(null, [latest]);
        return asset ?? throw new InvalidDataException("ReleaseParser did not select the MadokaChanger setup asset.");
    }

    private static Version ReleaseNormalize(Version version) => new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name, throwOnError: true)!;

    private static object CreateNullLogger(AssemblyLoadContext context, Type category)
    {
        var logging = context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(category.Assembly.Location)!, "Microsoft.Extensions.Logging.Abstractions.dll"));
        var genericLogger = logging.GetType("Microsoft.Extensions.Logging.ILogger`1", throwOnError: true)!;
        var loggerContract = genericLogger.MakeGenericType(category);
        return DispatchProxy.Create(loggerContract, typeof(NullLoggerProxy));
    }

    private static async Task<object?> AwaitResultAsync(object taskObject)
    {
        var task = (Task)taskObject;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static object? Get(object value, string property) => value.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);

    private static string[] VerifyOldUpdateCommand(Assembly app, Assembly core)
    {
        var viewModel = app.GetType("MadokaChanger.App.ViewModels.MainViewModel", throwOnError: true)!;
        if (viewModel.GetProperty("UpdateCommand", BindingFlags.Public | BindingFlags.Instance) is null)
            throw new InvalidDataException("The saved v1.1.0 UI has no UpdateCommand property.");
        var service = core.GetType("MadokaChanger.Core.UpdateService", throwOnError: true)!;
        var required = new HashSet<string>(StringComparer.Ordinal) { "CheckAsync", "DownloadAppUpdateAsync", "LaunchAppSetup" };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in AllNestedTypes(viewModel).Where(type => type.Name.Contains("UpdateAsync", StringComparison.Ordinal)))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (var target in CalledMethods(method))
                if (target.DeclaringType == service && required.Contains(target.Name)) found.Add(target.Name);
        }
        if (!found.SetEquals(required)) throw new InvalidDataException("The old UpdateCommand IL did not contain the expected check, download and installer launch routes: " + string.Join(", ", found));
        return found.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<Type> AllNestedTypes(Type type)
    {
        yield return type;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var descendant in AllNestedTypes(nested)) yield return descendant;
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!)
            .GroupBy(op => unchecked((ushort)op.Value)).ToDictionary(group => group.Key, group => group.First());
        for (var offset = 0; offset < il.Length;)
        {
            var first = il[offset++];
            var code = first == 0xFE ? (ushort)(0xFE00 | il[offset++]) : first;
            if (!codes.TryGetValue(code, out var op)) yield break;
            if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
            {
                var token = BitConverter.ToInt32(il, offset);
                MethodBase? called = null;
                try { called = method.Module.ResolveMethod(token, method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments()); } catch (ArgumentException) { }
                if (called is not null) yield return called;
            }
            offset += OperandLength(op.OperandType, il, offset);
        }
    }

    private static int OperandLength(OperandType operand, byte[] il, int offset) => operand switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, offset) * 4,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        _ => throw new NotSupportedException($"Unsupported IL operand {operand}.")
    };

    private sealed class OldReleaseLoadContext(string directory) : AssemblyLoadContext("MadokaChanger.old-release", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Keep framework type identity shared with this harness (notably HttpClient,
            // Task, JsonElement, and WPF). The saved release folder is self-contained and
            // includes framework assemblies, so probing it indiscriminately breaks reflection.
            var name = assemblyName.Name ?? string.Empty;
            if (name.StartsWith("System.", StringComparison.Ordinal)
                || name is "System" or "netstandard" or "WindowsBase" or "PresentationCore" or "PresentationFramework"
                || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal))
                return null;

            var path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}

public class NullLoggerProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "IsEnabled") return false;
        var returnType = targetMethod?.ReturnType;
        return returnType is { IsValueType: true } && returnType != typeof(void) ? Activator.CreateInstance(returnType) : null;
    }
}
