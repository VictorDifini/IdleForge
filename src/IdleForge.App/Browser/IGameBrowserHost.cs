using IdleForge.Core;

namespace IdleForge.App.Browser;

public interface IGameBrowserHost
{
    Task StartAsync(GameDefinition game, GameProfile profile, IReadOnlyList<UserScript> scripts, CancellationToken cancellationToken = default);
    Task NavigateAsync(Uri uri);
    Task ReloadAsync();
    Task SuspendAsync();
    Task ResumeAsync();
}
