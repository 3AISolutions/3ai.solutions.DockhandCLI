namespace DockhandCLI.Presentation;

/// <summary>Synchronous <see cref="IProgress{T}"/>; unlike <see cref="Progress{T}"/> it keeps event order.</summary>
internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
