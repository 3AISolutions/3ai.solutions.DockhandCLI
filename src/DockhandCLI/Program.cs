using System.Text;
using DockhandCLI.Hosting;
using DockhandCLI.Infrastructure.SelfManagement;
using Microsoft.Extensions.DependencyInjection;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached; output is redirected.
}

InstallLocation.DeleteLeftovers();

await using var services = CliHost.BuildServices();
var context = services.GetRequiredService<InvocationContext>();
using var cancellation = new CancellationTokenSource();

// First Ctrl+C cancels the running command gracefully; a second one terminates the process.
// Inside the interactive shell, the shell handles Ctrl+C per command instead.
Console.CancelKeyPress += (_, e) =>
{
    if (context.InShell || cancellation.IsCancellationRequested)
    {
        return;
    }

    e.Cancel = true;
    cancellation.Cancel();
};

return await services.GetRequiredService<CliApplication>().RunAsync(args, cancellation.Token);
