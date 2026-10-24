using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace DockhandCLI.Hosting;

/// <summary>
/// Bridges Spectre.Console.Cli to Microsoft.Extensions.DependencyInjection. Application services come
/// from one root provider built at startup; commands are created per invocation.
/// </summary>
internal sealed class TypeRegistrar(IServiceProvider root) : ITypeRegistrar
{
    private readonly Dictionary<Type, Func<object>> _registrations = [];

    public void Register(Type service, Type implementation) =>
        _registrations[service] = () => ActivatorUtilities.CreateInstance(root, implementation);

    public void RegisterInstance(Type service, object implementation) =>
        _registrations[service] = () => implementation;

    public void RegisterLazy(Type service, Func<object> factory) =>
        _registrations[service] = factory;

    public ITypeResolver Build() => new TypeResolver(root, _registrations);

    private sealed class TypeResolver(IServiceProvider root, Dictionary<Type, Func<object>> registrations) : ITypeResolver
    {
        public object? Resolve(Type? type)
        {
            if (type is null)
            {
                return null;
            }

            if (registrations.TryGetValue(type, out var factory))
            {
                return factory();
            }

            var service = root.GetService(type);
            if (service is not null)
            {
                return service;
            }

            return type is { IsAbstract: false, IsInterface: false } ? ActivatorUtilities.CreateInstance(root, type) : null;
        }
    }
}
