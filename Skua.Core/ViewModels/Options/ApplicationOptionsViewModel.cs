namespace Skua.Core.ViewModels;

public class ApplicationOptionsViewModel : BotControlViewModelBase
{
    public ApplicationOptionsViewModel(List<DisplayOptionItemViewModelBase> appOptions)
        : base("Application Options", 420, 500)
    {
        ApplicationOptions = appOptions;
    }

    public List<DisplayOptionItemViewModelBase> ApplicationOptions { get; }
}