using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

internal static class CommandRegistration
{
    public static void Register(IConfigurator config)
    {
        config.AddExample("endpoint", "add", "srv01", "https://docker01.example.com");
        config.AddExample("-srv01", "check", "updates");
        config.AddExample("-srv01", "--all-envs", "check", "updates", "--output", "json");
        config.AddExample("-srv01", "check", "apply", "update", "docker", "nginx", "--dry-run");
        config.AddExample("containers", "search", "myapp");
        config.AddExample("-srv01", "--env", "hhdocker01", "update", "container", "myapp", "--yes");

        config.AddBranch("endpoint", endpoint =>
        {
            endpoint.SetDescription("Manage saved Dockhand API endpoints");
            endpoint.AddCommand<EndpointAddCommand>("add").WithDescription("Register a Dockhand installation")
                .WithExample("endpoint", "add", "srv01", "https://docker01.example.com", "--description", "Production");
            endpoint.AddCommand<EndpointListCommand>("list").WithAlias("ls").WithDescription("List registered endpoints");
            endpoint.AddCommand<EndpointShowCommand>("show").WithDescription("Show an endpoint and its masked token");
            endpoint.AddCommand<EndpointRemoveCommand>("remove").WithAlias("rm").WithDescription("Remove an endpoint and its stored token");
            endpoint.AddCommand<EndpointDefaultCommand>("default").WithDescription("Set the default endpoint");
            endpoint.AddCommand<EndpointSetEnvCommand>("set-env").WithDescription("Save the default Docker environment of an endpoint")
                .WithExample("endpoint", "set-env", "srv01", "1");
            endpoint.AddCommand<EndpointTestCommand>("test").WithDescription("Check connectivity and authentication");
            endpoint.AddCommand<EndpointAuthCommand>("auth").WithDescription("Store an API token: paste one, or sign in to create one")
                .WithExample("endpoint", "auth", "srv01")
                .WithExample("endpoint", "auth", "srv01", "--token-stdin")
                .WithExample("endpoint", "auth", "srv01", "--login", "--username", "admin");
            endpoint.AddCommand<EndpointLogoutCommand>("logout").WithDescription("Remove the stored token of an endpoint");
        });

        config.AddBranch("env", env =>
        {
            env.SetDescription("Docker environments of an endpoint");
            env.AddCommand<EnvListCommand>("list").WithAlias("ls").WithDescription("List Docker environments")
                .WithExample("-srv01", "env", "list");
            env.AddCommand<EnvShowCommand>("show").WithDescription("Show one Docker environment")
                .WithExample("-srv01", "env", "show", "1");
        });

        config.AddBranch("containers", containers =>
        {
            containers.SetDescription("Containers of a Docker environment");
            containers.AddCommand<ContainerListCommand>("list").WithAlias("ls").WithDescription("List containers")
                .WithExample("-srv01", "--env", "1", "containers", "list");
            containers.AddCommand<ContainerSearchCommand>("search").WithAlias("find")
                .WithDescription("Find containers by name or image in every environment, with the command to update each")
                .WithExample("containers", "search", "myapp")
                .WithExample("containers", "search", "registry.example.com/myapp", "--all-endpoints");
            containers.AddCommand<ContainerInspectCommand>("inspect").WithDescription("Show the Docker inspect data (JSON)");
            containers.AddCommand<ContainerLogsCommand>("logs").WithDescription("Show container logs")
                .WithExample("-srv01", "containers", "logs", "nginx", "--tail", "50");
            containers.AddCommand<ContainerStartCommand>("start").WithDescription("Start a container");
            containers.AddCommand<ContainerStopCommand>("stop").WithDescription("Stop a container");
            containers.AddCommand<ContainerRestartCommand>("restart").WithDescription("Restart a container");
            containers.AddCommand<ContainerPauseCommand>("pause").WithDescription("Pause a container");
            containers.AddCommand<ContainerUnpauseCommand>("unpause").WithDescription("Unpause a container");
        });

        config.AddBranch("check", check =>
        {
            check.SetDescription("Container image update checks");
            check.AddCommand<CheckUpdatesCommand>("updates").WithDescription("Check containers for image updates")
                .WithExample("-srv01", "check", "updates")
                .WithExample("-srv01", "check", "updates", "--pending")
                .WithExample("-srv01", "--all-envs", "check", "updates");
            check.AddBranch("apply", apply =>
            {
                apply.SetDescription("Apply updates");
                apply.AddBranch("update", update =>
                {
                    update.SetDescription("Apply an update");
                    update.AddCommand<ApplyUpdateCommand>("docker").WithDescription("Update a container to its latest image")
                        .WithExample("-srv01", "check", "apply", "update", "docker", "nginx", "--dry-run")
                        .WithExample("-srv01", "check", "apply", "update", "docker", "nginx", "--yes");
                });
            });
        });

        config.AddBranch("update", update =>
        {
            update.SetDescription("Container updates (short form)");
            update.AddCommand<ApplyUpdateCommand>("apply").WithDescription("Update a container to its latest image")
                .WithExample("-srv01", "update", "apply", "nginx");
            update.AddCommand<UpdateContainerCommand>("container")
                .WithDescription("Check one container for a new image and update it if there is one")
                .WithExample("-srv01", "--env", "hhdocker01", "update", "container", "myapp")
                .WithExample("-srv01", "--env", "hhdocker01", "update", "container", "myapp", "--check-only")
                .WithExample("-srv01", "--env", "hhdocker01", "update", "container", "myapp", "--yes", "--output", "json");
        });

        config.AddBranch("images", images =>
        {
            images.SetDescription("Docker images");
            images.AddCommand<ImageListCommand>("list").WithAlias("ls").WithDescription("List images");
        });

        config.AddBranch("stacks", stacks =>
        {
            stacks.SetDescription("Compose stacks");
            stacks.AddCommand<StackListCommand>("list").WithAlias("ls").WithDescription("List stacks");
            stacks.AddCommand<StackShowCommand>("show").WithDescription("Show a stack and its compose file");
            stacks.AddCommand<StackStartCommand>("start").WithDescription("Start a stack");
            stacks.AddCommand<StackStopCommand>("stop").WithDescription("Stop a stack");
            stacks.AddCommand<StackRestartCommand>("restart").WithDescription("Restart a stack (--mode restart|ordered|recreate)");
            stacks.AddCommand<StackDeployCommand>("deploy").WithDescription("Deploy a stack (docker compose up)")
                .WithExample("-srv01", "stacks", "deploy", "web", "--pull");
            stacks.AddCommand<StackDownCommand>("down").WithDescription("Take a stack down (docker compose down)");
        });

        config.AddBranch("jobs", jobs =>
        {
            jobs.SetDescription("Background jobs of long-running operations");
            jobs.AddCommand<JobShowCommand>("show").WithDescription("Show a job's status and result");
            jobs.AddCommand<JobCancelCommand>("cancel").WithDescription("Request cancellation of a running job");
        });

        config.AddBranch("self", self =>
        {
            self.SetDescription("Install and update dockhand itself");
            self.AddCommand<SelfPathCommand>("path").WithDescription("Add this executable's folder to your PATH (replacing older dockhand entries)")
                .WithExample("self", "path")
                .WithExample("self", "path", "--remove");
            self.AddCommand<SelfUpdateCommand>("update").WithDescription("Update dockhand to the latest release")
                .WithExample("self", "update", "--check")
                .WithExample("self", "update");
        });

        config.AddCommand<HealthCommand>("health").WithDescription("Check the health of an endpoint (no token needed)");
        config.AddCommand<SystemCommand>("system").WithDescription("Show Dockhand host, runtime and Docker information");
    }
}
