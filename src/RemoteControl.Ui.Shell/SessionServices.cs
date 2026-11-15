namespace RemoteControl.Ui.Shell;

/// <summary>
/// What the groups of windows offer each other for one session, by interface: the main window offers <see cref="ISessionChat"/>,
/// the players group <see cref="IPrivateMessages"/>, and so on. A group asks with <see cref="Get{T}"/> and must cope with null
/// (the other group's window may not be open, or the group may not be part of the program). Used on the UI thread only.
/// </summary>
public sealed class SessionServices
{
    private readonly Dictionary<Type, object> _services = [];

    /// <summary>Raised when a service was offered or withdrawn; the argument is the interface.</summary>
    public event EventHandler<Type>? Changed;

    /// <summary>Offers a service; it replaces an earlier one of the same interface.</summary>
    /// <typeparam name="T">The interface.</typeparam>
    /// <param name="service">The object that implements it.</param>
    /// <returns>Dispose it to withdraw the service (when the offering window closes).</returns>
    public IDisposable Provide<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        _services[typeof(T)] = service;
        Changed?.Invoke(this, typeof(T));
        return new Withdrawal(() =>
        {
            if (_services.TryGetValue(typeof(T), out object? current) && ReferenceEquals(current, service))
            {
                _services.Remove(typeof(T));
                Changed?.Invoke(this, typeof(T));
            }
        });
    }

    /// <summary>The service offered for an interface, or null.</summary>
    /// <typeparam name="T">The interface.</typeparam>
    /// <returns>The service, or null.</returns>
    public T? Get<T>() where T : class => _services.TryGetValue(typeof(T), out object? service) ? (T)service : null;

    private sealed class Withdrawal(Action withdraw) : IDisposable
    {
        private Action? _withdraw = withdraw;

        public void Dispose() => Interlocked.Exchange(ref _withdraw, null)?.Invoke();
    }
}
