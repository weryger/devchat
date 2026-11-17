using System.Runtime.InteropServices;

namespace DevChat.App.Services;

/// <summary>
/// Управление сном ПК: пока приложение запущено (и опция включена) — система не засыпает.
/// SetThreadExecutionState не меняет настройки питания Windows и автоматически сбрасывается ОС при выходе.
/// </summary>
public static class PowerService
{
    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(EXECUTION_STATE esFlags);

    /// <summary>Запретить (on=true) или вернуть (on=false) системный сон для этого процесса.</summary>
    public static void PreventSleep(bool on) =>
        SetThreadExecutionState(on
            ? EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED
            : EXECUTION_STATE.ES_CONTINUOUS);
}
