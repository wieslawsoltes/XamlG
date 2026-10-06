namespace XamlG.Runtime;

/// <summary>One weakly-owned component/document initialization state. Failed attempts are
/// retryable, same-thread recursion is rejected, and concurrent callers observe one completed initialization.</summary>
public sealed class XamlComponentInitializationState
{
    private readonly object _gate = new();
    private byte _state;

    public void Initialize<T>(T instance, IServiceProvider? services, Action<T, IServiceProvider?> populate) where T : class
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        if (populate == null) throw new ArgumentNullException(nameof(populate));
        lock (_gate)
        {
            if (_state == 2) return;
            if (_state == 1) throw new InvalidOperationException("Reentrant XAML component initialization.");
            _state = 1;
            try
            {
                populate(instance, services ?? XamlConstructionScope.GetServicesFor(instance));
                XamlConstructionScope.RegisterInitialized(typeof(T), instance);
                _state = 2;
            }
            catch
            {
                _state = 0;
                throw;
            }
        }
    }
}
