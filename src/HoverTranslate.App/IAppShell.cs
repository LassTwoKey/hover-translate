using HoverTranslate.Core.Models;

namespace HoverTranslate.App;

/// <summary>Действия с окнами приложения; реализуется App, вызывается из любого потока.</summary>
public interface IAppShell
{
    void ShowDebugWindow();

    /// <summary>Окно «Словарь»: история и избранное.</summary>
    void ShowVocabularyWindow();

    /// <summary>Окно настроек.</summary>
    void ShowSettingsWindow();

    /// <summary>Показать итог нажатия горячей клавиши (пока — в отладочном окне вместо popup).</summary>
    void ShowOutcome(CaptureOutcome outcome);

    void Quit();
}
