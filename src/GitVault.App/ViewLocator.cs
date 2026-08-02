using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GitVault.App.ViewModels;

namespace GitVault.App;

/// <summary>
/// Resolves a view for a view model by naming convention, walking up the base-type chain so
/// that a family of view models can share one view.
/// </summary>
internal sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? param)
    {
        if (param is null)
        {
            return new TextBlock { Text = string.Empty };
        }

        for (var type = param.GetType(); type is not null; type = type.BaseType)
        {
            var name = type.FullName?.Replace("ViewModels.", "Views.", StringComparison.Ordinal)
                                     .Replace("ViewModel", "View", StringComparison.Ordinal);
            if (name is null)
            {
                continue;
            }

            var viewType = Type.GetType(name);
            if (viewType is not null && Activator.CreateInstance(viewType) is Control control)
            {
                return control;
            }
        }

        // Reaching here is a wiring bug, not a user-facing state: show the type name only,
        // so there is nothing to translate and the missing view is still identifiable.
        return new TextBlock { Text = param.GetType().Name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
