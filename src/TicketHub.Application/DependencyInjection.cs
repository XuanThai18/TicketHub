using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace TicketHub.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(config =>
        {
            config.RegisterServicesFromAssembly(assembly);
            // Đăng ký Pipeline Behavior theo thứ tự: Log trước, Validate sau
            config.AddOpenBehavior(typeof(Behaviors.LoggingBehavior<,>));
            config.AddOpenBehavior(typeof(Behaviors.ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(assembly);

        return services;
    }
}