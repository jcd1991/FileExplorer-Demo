using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TestProject.Controllers;

[AttributeUsage(AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        for (var index = factories.Count - 1; index >= 0; index--)
        {
            if (factories[index] is FormValueProviderFactory or FormFileValueProviderFactory or JQueryFormValueProviderFactory)
            {
                factories.RemoveAt(index);
            }
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
