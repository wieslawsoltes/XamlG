using System;
using System.ComponentModel;
using MiniMvvm;
using Xunit;

namespace ControlCatalog.Tests;

public sealed class MiniMvvmTests
{
    [Fact]
    public void CompletionCanDisposeItsSubscriptionReentrantly()
    {
        var model = new Model();
        IDisposable? subscription = null;
        var completed = 0;
        var values = 0;
        var observer = new Observer(() => values++, () => { completed++; subscription!.Dispose(); });
        subscription = model.WhenAnyValue(value => value.Value).Subscribe(observer);
        Assert.Equal(1, values);
        subscription.Dispose();
        subscription.Dispose();
        model.Change();
        Assert.Equal(1, completed);
        Assert.Equal(1, values);
    }

    private sealed class Model : INotifyPropertyChanged
    {
        public bool Value => true;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Change() => PropertyChanged?.Invoke(this, new(nameof(Value)));
    }

    private sealed class Observer(Action next, Action completed) : IObserver<bool>
    {
        public void OnNext(bool value) => next();
        public void OnCompleted() => completed();
        public void OnError(Exception error) => throw error;
    }
}
