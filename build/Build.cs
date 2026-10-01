using Tamp;
using Tamp.Go;
using Tamp.NetCli.V10;

/// <summary>
/// tamp-observer's self-hosted build script. tamp drives the project's own pipeline (rule #3).
/// Run via <c>dotnet run --project build -- &lt;target&gt;</c>, or, after
/// <c>dotnet tool install -g Tamp.Cli</c>, via <c>tamp &lt;target&gt;</c>.
/// </summary>
class Build : TampBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    [Parameter("Build configuration")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution = null!;

    // ----- Go collector (ADR 0003) -----

    [FromPath("go")] readonly Tool GoBin = null!;

    const string OcbVersion = "v0.162.0";

    AbsolutePath Collector => RootDirectory / "src" / "collector";
    AbsolutePath CollectorDist => Collector / "_dist";
    AbsolutePath Artifacts => RootDirectory / "artifacts";
    string HostCollector => OperatingSystem.IsWindows() ? "tamp-observer-collector.exe" : "tamp-observer-collector";

    Target Info => _ => _
        .Description("Print build context.")
        .Executes(() =>
        {
            Console.WriteLine($"  Configuration: {Configuration}");
            Console.WriteLine($"  Solution:      {Solution.Name} ({Solution.Projects.Count} projects)");
            Console.WriteLine($"  Local build:   {IsLocalBuild}");
        });

    Target Clean => _ => _
        .Description("Delete bin/obj across the tree.")
        .Executes(() =>
        {
            foreach (var d in RootDirectory.GlobDirectories("**/bin", "**/obj"))
                d.Delete();
        });

    Target Restore => _ => _
        .Description("dotnet restore the solution.")
        .Executes(() => DotNet.Restore(s => s.SetProject(Solution.Path)));

    Target Compile => _ => _
        .DependsOn(nameof(Restore))
        .Description("dotnet build the solution.")
        .Executes(() => DotNet.Build(s => s
            .SetProject(Solution.Path)
            .SetConfiguration(Configuration)
            .SetNoRestore(true)));

    Target Test => _ => _
        .DependsOn(nameof(Compile))
        .Description("Run the test suite. Integration tests spin ephemeral Postgres via Testcontainers (Docker required).")
        .Executes(() => DotNet.Test(s => s
            .SetProject(Solution.Path)
            .SetConfiguration(Configuration)
            .SetNoBuild(true)));

    Target Ci => _ => _
        .DependsOn(nameof(Info), nameof(Clean), nameof(Test))
        .Description("Full pipeline: info, clean, restore, build, test.");

    // ----- Go collector targets (dogfood Tamp.Go, rule #3) -----

    Target CollectorGenerate => _ => _
        .Description("Generate the custom OTel Collector distribution with ocb (via `go run`), no compile.")
        .Executes(() => Go.Run_(GoBin, s => s
            .SetWorkingDirectory(Collector)
            .AddPackage($"go.opentelemetry.io/collector/cmd/builder@{OcbVersion}")
            .AddProgramArgs("--config", "otelcol-builder.yaml", "--skip-compilation")));

    Target CollectorBuild => _ => _
        .DependsOn(nameof(CollectorGenerate))
        .Description("Compile the generated collector for the host (so `components` can be smoke-run).")
        .Executes(() => Go.Build(GoBin, s => s
            .SetWorkingDirectory(CollectorDist)
            .SetCgoEnabled(false)
            .SetTrimpath()
            .SetOutput((Artifacts / HostCollector).Value)
            .AddPackage(".")));

    Target CollectorBuildLinux => _ => _
        .DependsOn(nameof(CollectorGenerate))
        .Description("Cross-compile the collector for the Linux pod target (ADR 0003: linux, CGO disabled, static).")
        .Executes(() => Go.Build(GoBin, s => s
            .SetWorkingDirectory(CollectorDist)
            .SetPlatform("linux", "amd64")
            .SetCgoEnabled(false)
            .SetTrimpath()
            .StripDebugInfo()
            .SetOutput((Artifacts / "tamp-observer-collector-linux-amd64").Value)
            .AddPackage(".")));
}
