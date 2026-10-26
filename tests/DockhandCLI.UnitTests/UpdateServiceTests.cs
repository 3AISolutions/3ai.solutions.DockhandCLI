using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;

namespace DockhandCLI.UnitTests;

public class UpdateServiceTests
{
    private readonly UpdateService _service = new();
    private readonly FakeApi _api = new();

    private Task<UpdatePlan> PlanAsync(string container = "nginx") =>
        _service.PlanAsync(_api, _api.Environments[0], container);

    [Fact]
    public async Task Dry_run_never_calls_the_update_api()
    {
        var plan = await PlanAsync();
        var prompt = new FakePrompt(canPrompt: true, answer: true);

        var outcome = await _service.ApplyAsync(_api, plan, dryRun: true, assumeYes: false, prompt);

        Assert.True(outcome.DryRun);
        Assert.False(outcome.Executed);
        Assert.Empty(_api.BatchUpdateCalls);
        Assert.Equal(0, prompt.Questions);
    }

    [Fact]
    public async Task Confirmation_is_required_without_yes_when_not_interactive()
    {
        var plan = await PlanAsync();

        var ex = await Assert.ThrowsAsync<DockhandException>(() => _service.ApplyAsync(_api, plan, false, false, new FakePrompt(canPrompt: false)));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("--yes", ex.Message);
        Assert.Empty(_api.BatchUpdateCalls);
    }

    [Fact]
    public async Task Declined_confirmation_aborts_without_calling_the_api()
    {
        var plan = await PlanAsync();
        var prompt = new FakePrompt(canPrompt: true, answer: false);

        var ex = await Assert.ThrowsAsync<DockhandException>(() => _service.ApplyAsync(_api, plan, false, false, prompt));

        Assert.Equal(ExitCodes.Aborted, ex.ExitCode);
        Assert.Equal(1, prompt.Questions);
        Assert.Empty(_api.BatchUpdateCalls);
    }

    [Fact]
    public async Task Accepted_confirmation_updates_exactly_the_resolved_container()
    {
        var plan = await PlanAsync();

        var outcome = await _service.ApplyAsync(_api, plan, false, false, new FakePrompt(canPrompt: true, answer: true));

        Assert.True(outcome.Success);
        var call = Assert.Single(_api.BatchUpdateCalls);
        Assert.Equal(["a1b2c3d4e5f6a7b8c9d0"], call);
    }

    [Fact]
    public async Task Yes_skips_the_question()
    {
        var plan = await PlanAsync();
        var prompt = new FakePrompt(canPrompt: false);

        var outcome = await _service.ApplyAsync(_api, plan, false, assumeYes: true, prompt);

        Assert.True(outcome.Success);
        Assert.Equal(0, prompt.Questions);
        Assert.Single(_api.BatchUpdateCalls);
    }

    [Fact]
    public async Task Reported_failure_is_not_success()
    {
        _api.BatchResponse = new BatchUpdateResponse
        {
            Success = false,
            Results = [new BatchUpdateItem { ContainerId = "a1b2c3d4e5f6a7b8c9d0", ContainerName = "nginx", Success = false, Error = "pull failed" }],
        };
        var plan = await PlanAsync();

        var outcome = await _service.ApplyAsync(_api, plan, false, true, new FakePrompt(false));

        Assert.False(outcome.Success);
        Assert.Equal("pull failed", outcome.Error);
    }

    [Fact]
    public async Task Missing_result_for_container_is_not_reported_as_success()
    {
        _api.BatchResponse = new BatchUpdateResponse { Success = true, Results = [] };
        var plan = await PlanAsync();

        var outcome = await _service.ApplyAsync(_api, plan, false, true, new FakePrompt(false));

        Assert.False(outcome.Success);
        Assert.Contains("unknown", outcome.Error);
    }

    [Fact]
    public async Task Plan_includes_pending_update_record()
    {
        _api.Pending = new PendingUpdatesResponse
        {
            EnvironmentId = 1,
            PendingUpdates = [new PendingUpdate { ContainerId = "a1b2c3d4e5f6a7b8c9d0", ContainerName = "nginx", HasImageUpdate = true }],
        };

        var plan = await PlanAsync();

        Assert.True(plan.PendingUpdate?.HasImageUpdate);
        Assert.True(plan.PendingStateKnown);
    }

    [Fact]
    public async Task Ambiguous_container_never_reaches_the_api()
    {
        _api.Containers.Add(new ContainerSummary { Id = "nginx", Name = "web" });

        await Assert.ThrowsAsync<DockhandException>(() => PlanAsync("nginx"));
        Assert.Empty(_api.BatchUpdateCalls);
    }

    [Theory]
    [InlineData(true, null, null, null, UpdateStatus.UpdateAvailable)]
    [InlineData(false, null, null, null, UpdateStatus.UpToDate)]
    [InlineData(true, "registry timeout", null, null, UpdateStatus.Error)]
    [InlineData(true, null, true, null, UpdateStatus.Disabled)]
    [InlineData(true, null, null, "dockhand", UpdateStatus.SystemContainer)]
    public void Maps_fresh_check_results(bool hasUpdate, string? error, bool? disabled, string? system, string expected)
    {
        var row = UpdateService.FromCheckResult(new UpdateCheckResult
        {
            ContainerId = "id",
            ContainerName = "nginx",
            HasUpdate = hasUpdate,
            Error = error,
            UpdateDisabled = disabled,
            SystemContainer = system,
        });

        Assert.Equal(expected, row.Status);
    }

    [Fact]
    public void Maps_held_and_newer_tag_results()
    {
        Assert.Equal(UpdateStatus.Held, UpdateService.FromCheckResult(new UpdateCheckResult { ReleaseAgeRemainingHours = 12 }).Status);
        Assert.Equal(UpdateStatus.NewerTag, UpdateService.FromCheckResult(new UpdateCheckResult { NewerVersion = new NewerVersion { Tag = "1.27" } }).Status);
        Assert.Equal(UpdateStatus.UpdateAvailable, UpdateService.FromPending(new PendingUpdate { HasImageUpdate = true }, null).Status);
    }

    [Fact]
    public async Task All_environment_check_reports_partial_failures()
    {
        _api.Environments.Add(new DockerEnvironment { Id = 2, Name = "hhdocker02" });
        _api.CheckResult = id => id == 1
            ? new UpdateCheckSummary { Total = 1, UpdatesFound = 1, Results = [new UpdateCheckResult { ContainerId = "x", ContainerName = "nginx", HasUpdate = true }] }
            : throw new DockhandException(ExitCodes.Network, "unreachable");

        var report = await _service.CheckAsync(_api, _api.Environments, UpdateCheckMode.Fresh);

        Assert.Equal(2, report.Environments.Count);
        Assert.Null(report.Environments[0].Error);
        Assert.Equal("unreachable", report.Environments[1].Error);
        Assert.Equal(ExitCodes.PartialFailure, report.ExitCode);
    }

    [Fact]
    public async Task Single_environment_failure_propagates()
    {
        await Assert.ThrowsAsync<DockhandException>(() => _service.CheckAsync(_api, _api.Environments, UpdateCheckMode.Fresh));
    }
}

public class CommandLinesTests
{
    private static readonly List<DockerEnvironment> Environments =
    [
        new() { Id = 1, Name = "hhdocker01" },
        new() { Id = 2, Name = "Prod Server" },
        new() { Id = 3, Name = "42" },
        new() { Id = 4, Name = "dup" },
        new() { Id = 5, Name = "DUP" },
    ];

    [Theory]
    [InlineData(1, "hhdocker01")]
    [InlineData(2, "2")]
    [InlineData(3, "3")]
    [InlineData(4, "4")]
    public void Environment_name_is_used_only_when_it_is_a_plain_unique_token(int id, string expected) =>
        Assert.Equal(expected, CommandLines.EnvironmentSelector(Environments.Single(e => e.Id == id), Environments));

    [Fact]
    public void Update_command_uses_the_alias_shorthand_and_falls_back_to_the_container_id()
    {
        var plain = new ContainerSummary { Id = "abc123", Name = "/myapp" };
        var odd = new ContainerSummary { Id = "def456", Name = "my app" };

        Assert.Equal("dockhand -srv01 --env hhdocker01 update container myapp", CommandLines.UpdateContainer("srv01", "hhdocker01", plain));
        Assert.Equal("dockhand -srv01 --env 2 update container def456", CommandLines.UpdateContainer("srv01", "2", odd));
    }

    [Theory]
    [InlineData("APP", true)]
    [InlineData("registry.example.com/my", true)]
    [InlineData("redis", false)]
    public void Search_matches_name_or_image_ignoring_case(string term, bool expected) =>
        Assert.Equal(expected, ContainerSearch.IsMatch(new ContainerSummary { Name = "/myapp", Image = "registry.example.com/myapp:latest" }, term));
}
