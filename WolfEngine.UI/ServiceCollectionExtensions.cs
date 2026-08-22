using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Rendering.UI;

namespace WolfEngine.UI;

public static class ServiceCollectionExtensions
{
	public static IServiceCollection AddWolfEngineGameplayUi(this IServiceCollection services)
	{
		services.AddSingleton<GameplayUiHost>();
		services.AddSingleton<IGameplayUiHost>(provider => provider.GetRequiredService<GameplayUiHost>());
		services.AddSingleton<IGameplayUiFrameProvider>(provider => provider.GetRequiredService<GameplayUiHost>());
		return services;
	}
}
