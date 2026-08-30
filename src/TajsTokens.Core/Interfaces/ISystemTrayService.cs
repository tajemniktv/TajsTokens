namespace TajsTokens.Core.Interfaces;

public interface ISystemTrayService
{
    void Initialize();
    void ShowNotification(string title, string message);
}
