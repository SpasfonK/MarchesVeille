namespace MarchesVeille.Core.Util;

/// <summary>IProgress synchrone (contrairement à Progress&lt;T&gt; qui poste sur le contexte).</summary>
public sealed class DelegateProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public DelegateProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}
