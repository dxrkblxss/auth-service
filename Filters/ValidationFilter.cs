using System.ComponentModel.DataAnnotations;

namespace AuthService.Filters;

public class ValidationFilter<T> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var arg = context.Arguments.FirstOrDefault(a => a is T);
        if (arg is not T model)
            return await next(context);

        var validationContext = new ValidationContext(model);
        var results = new List<ValidationResult>();

        if (!Validator.TryValidateObject(model, validationContext, results, true))
        {
            var errors = results.ToDictionary(
                x => x.MemberNames.FirstOrDefault() ?? "Error",
                x => new[] { x.ErrorMessage ?? "Validation error" }
            );

            return Results.ValidationProblem(errors);
        }

        return await next(context);
    }
}