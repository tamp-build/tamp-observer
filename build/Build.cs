using Tamp;
using Tamp.Go;
using Tamp.Helm.V3;
using Tamp.NetCli.V10;
using Tamp.Npm.V10;
using Tamp.SonarScanner.V10;

/// <summary>
/// tamp-observer's self-hosted build script. tamp drives the project's own pipeline (rule #3).
/// Run via <c>dotnet run --project build -- &lt;target&gt;</c>, or, after
/// <c>dotnet tool install -g Tamp.Cli</c>, via <c>tamp &lt;target&gt;</c>.
/// </summary>
class Build : TampBuild
{
    public static int Main(string[] args)
    {
        // Export the build's own ADR 0018 telemetry to tamp-observer when a collector is configured (CI dogfood).
        using var telemetry = BuildTelemetry.Start();
        return Execute<Build>(args);
    }

    [Parameter("Build configuration")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution = null!;

    // ----- SonarCloud (SonarQube Cloud) -----

    // dotnet-sonarscanner is a DLL-based .NET tool; install it globally in CI
    // (`dotnet tool install --global dotnet-sonarscanner`) and resolve the apphost from PATH.
    // Optional so the fast unit lane (which never runs Sonar) does not require it to be installed.
    [FromPath("dotnet-sonarscanner", Optional = true)]
    readonly Tool SonarTool = null!;

    [Secret("SonarCloud token", EnvironmentVariable = "SONAR_TOKEN")]
    readonly Secret SonarToken = null!;

    [Parameter("Sonar host URL", EnvironmentVariable = "SONAR_HOST_URL")]
    readonly string SonarHostUrl = "https://sonarcloud.io";

    [Parameter("SonarCloud organization")]
    readonly string SonarOrganization = "tamp-build";

    [Parameter("SonarCloud project key")]
    readonly string SonarProjectKey = "tamp-build_tamp-observer";

    AbsolutePath CoverageDir => RootDirectory / "artifacts" / "coverage";

    // ----- Go collector (ADR 0003) -----

    // Optional so non-Go lanes (unit tests) do not require the Go toolchain on PATH.
    [FromPath("go", Optional = true)] readonly Tool GoBin = null!;

    // Optional so .NET-only lanes do not require Node/npm on PATH (ADR 0014: Svelte frontend, rule #3 dogfood).
    [FromPath("npm", Optional = true)] readonly Tool NpmBin = null!;

    AbsolutePath Frontend => RootDirectory / "frontend";

    // Optional so .NET-only lanes do not require helm on PATH (k8s deploy chart, rule #3 dogfood).
    [FromPath("helm", Optional = true)] readonly Tool HelmBin = null!;

    AbsolutePath Chart => RootDirectory / "deploy" / "helm" / "tamp-observer";

    const string OcbVersion = "v0.162.0";

    AbsolutePath Collector => RootDirectory / "src" / "collector";
    AbsolutePath CollectorDist => Collector / "_dist";
    AbsolutePath Artifacts => RootDirectory / "artifacts";
    AbsolutePath GoCoverage => CoverageDir / "go" / "rawfileexporter.coverage.out";
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
        .Description("Run the full test suite (unit + integration) with coverage. Integration tests need Docker.")
        .Executes(() => DotNet.Test(s => TestSettings(s)));

    Target UnitTest => _ => _
        .DependsOn(nameof(Compile))
        .Description("Run unit tests only (no Testcontainers; fast, for local builds and PR checks).")
        .Executes(() => DotNet.Test(s => TestSettings(s).SetFilter("Category!=Integration")));

    Target IntegrationTest => _ => _
        .DependsOn(nameof(Compile))
        .Description("Run integration tests only (Testcontainers: Postgres/ClickHouse/Valkey). Nightly.")
        .Executes(() => DotNet.Test(s => TestSettings(s).SetFilter("Category=Integration")));

    DotNetTestSettings TestSettings(DotNetTestSettings s) => s
        .SetProject(Solution.Path)
        .SetConfiguration(Configuration)
        .SetNoBuild(true)
        .AddDataCollector("XPlat Code Coverage")
        .SetSettings((RootDirectory / "build" / "coverlet.runsettings").Value)
        .SetResultsDirectory(CoverageDir);

    Target Ci => _ => _
        .DependsOn(nameof(Info), nameof(UnitTest))
        .Description("Fast lane (PR / push): info, build, unit tests only. (No Clean: it would delete the running build app's own bin.)");

    // SonarCloud analysis is a two-phase scan: Begin before the build, End after tests, with the
    // build and tests running between so the scanner collects MSBuild inputs and coverage.

    Target SonarBegin => _ => _
        .Description("Initialize the SonarCloud pre-build phase.")
        .Before(nameof(Compile))
        .Requires(() => SonarToken != null)
        .Executes(() => SonarScanner.Begin(SonarTool, s => s
            .SetProjectKey(SonarProjectKey)
            .SetOrganization(SonarOrganization)
            .SetHostUrl(SonarHostUrl)
            .SetToken(SonarToken)
            // build/**            — build tooling (NUKE-style DSL), not shipped product code.
            // src/*.TestIngestor/** — a synthetic-telemetry generator (IsPackable=false, Exe) used
            //                       to put fake spans through the pipeline during development. Its
            //                       findings are noise in every category: csharpsquid:S2245 fires on
            //                       `new Random(1337)`, whose fixed seed is the entire point, since
            //                       generated telemetry has to be reproducible. Excluding the project
            //                       is more honest than suppressing that rule one call at a time.
            .SetProperty("sonar.exclusions",
                // build/**            — build tooling, not shipped product code.
                // TestIngestor/**     — a synthetic-telemetry generator; its fixed RNG seed is the point.
                // design/**           — UI design mockups (19 standalone .html files, nothing references
                //                       them). They were 12.8% of the project's duplication and the entire
                //                       reason the gate's duplication condition failed: source/*.dc.html
                //                       and static/*.html are two renderings of the SAME page, so they
                //                       duplicate each other 40-90% BY CONSTRUCTION. Measuring code
                //                       duplication across design mockups answers no question anyone has.
                "build/**,src/Tamp.Observer.TestIngestor/**,design/**")
            .SetProperty("sonar.cs.opencover.reportsPaths", $"{CoverageDir.Value}/**/coverage.opencover.xml")
            .SetProperty("sonar.go.coverage.reportPaths", GoCoverage.Value)
));

    Target SonarEnd => _ => _
        .After(nameof(Test), nameof(CollectorCoverageMap))
        .DependsOn(nameof(SonarBegin))
        .Description("Finalize SonarCloud and submit results.")
        .Executes(() => SonarScanner.End(SonarTool, s => s.SetToken(SonarToken)));

    Target Sonar => _ => _
        .DependsOn(nameof(SonarBegin), nameof(Test), nameof(CollectorCoverageMap), nameof(SonarEnd))
        .Description("Full SonarCloud analysis: begin, .NET build + test coverage, Go test coverage, end.");

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

    Target CollectorTest => _ => _
        .Description("Run the Go collector unit tests with coverage (feeds SonarCloud Go coverage).")
        .Executes(() =>
        {
            System.IO.Directory.CreateDirectory((CoverageDir / "go").Value);
            return Go.Test(GoBin, s => s
                .SetWorkingDirectory(Collector / "rawfileexporter")
                .SetCover()
                .SetCoverMode("atomic")
                .SetCoverProfile(GoCoverage.Value)
                .AllPackages());
        });

    Target CollectorCoverageMap => _ => _
        .DependsOn(nameof(CollectorTest))
        .Description("Rewrite Go coverage paths from module import paths to repo-relative paths for SonarCloud.")
        .Executes(() =>
        {
            // go writes coverage paths as module import paths; rewrite the module prefix to the
            // repo-relative source path so SonarCloud maps coverage onto the indexed Go files.
            var cov = GoCoverage.Value;
            var text = System.IO.File.ReadAllText(cov).Replace(
                "github.com/tamp-build/tamp-observer/collector/rawfileexporter/",
                "src/collector/rawfileexporter/",
                StringComparison.Ordinal);
            System.IO.File.WriteAllText(cov, text);
        });

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

    // ----- Svelte frontend targets (dogfood Tamp.Npm, rule #3; ADR 0014) -----

    Target FrontendRestore => _ => _
        .Description("Install frontend dependencies from the lockfile (npm ci).")
        .Executes(() => Npm.Ci(NpmBin, s => s.SetWorkingDirectory(Frontend)));

    Target FrontendApiClient => _ => _
        .DependsOn(nameof(Compile), nameof(FrontendRestore))
        .Description("Regenerate the typed TS client from the API's build-time OpenAPI document (ADR 0014).")
        .Executes(() => Npm.Run(NpmBin, s =>
        {
            s.SetWorkingDirectory(Frontend);
            s.Script = "gen:api";
        }));

    Target FrontendCheck => _ => _
        .DependsOn(nameof(FrontendApiClient))
        .Description("Type-check the Svelte SPA against the generated client (svelte-check).")
        .Executes(() => Npm.Run(NpmBin, s =>
        {
            s.SetWorkingDirectory(Frontend);
            s.Script = "check";
        }));

    Target FrontendTest => _ => _
        .DependsOn(nameof(FrontendCheck))
        .Description("Run the SPA's unit tests (vitest) over src/lib's pure logic.")
        .Executes(() => Npm.Run(NpmBin, s =>
        {
            s.SetWorkingDirectory(Frontend);
            // Plain `test`, not `test:coverage`. The coverage side is NOT wired into Sonar: the v8
            // provider does not remap through this project's TypeScript source maps and reports
            // every file at 0% lines even when its tests pass (format.ts came out 100% functions,
            // 100% branches, 0% lines), and istanbul aborts instrumenting the untested files.
            // Feeding Sonar a report that says 0% would be worse than feeding it nothing -- see the
            // note on tamp-observer#19. `npm run test:coverage` still works locally.
            s.Script = "test";
        }));

    Target FrontendBuild => _ => _
        .DependsOn(nameof(FrontendTest))
        .Description("Build the Svelte SPA to static assets in frontend/dist (ADR 0014).")
        .Executes(() => Npm.Run(NpmBin, s =>
        {
            s.SetWorkingDirectory(Frontend);
            s.Script = "build";
        }));

    // ----- Helm chart (dogfood Tamp.Helm, rule #3; k8s deploy) -----

    Target HelmLint => _ => _
        .Description("Lint the tamp-observer Helm chart (strict).")
        .Executes(() => Helm.Lint(HelmBin, s =>
        {
            s.Chart = Chart.Value;
            s.Strict = true;
        }));
}
