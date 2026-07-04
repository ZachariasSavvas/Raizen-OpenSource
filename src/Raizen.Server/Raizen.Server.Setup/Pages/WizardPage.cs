namespace Raizen.Server.Setup.Pages;

public abstract class WizardPage : UserControl
{
    public abstract string Title { get; }

    public virtual bool CanProceed => true;

    public event EventHandler? CanProceedChanged;

    protected void RaiseCanProceedChanged() =>
        CanProceedChanged?.Invoke(this, EventArgs.Empty);

    public virtual Task OnActivatedAsync() => Task.CompletedTask;

    public new virtual string? Validate() => null;
}
