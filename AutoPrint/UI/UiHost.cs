namespace AutoPrint.UI;

/// <summary>Referência à janela principal para diálogos nativos (pasta, etc.).</summary>
public static class UiHost
{
    public static Form? MainForm { get; set; }

    public static T? Invoke<T>(Func<T> action)
    {
        var form = MainForm;
        if (form is null || form.IsDisposed || !form.IsHandleCreated)
            return action();

        if (!form.InvokeRequired)
            return action();

        T? result = default;
        form.Invoke(() => result = action());
        return result;
    }

    public static void Invoke(Action action)
    {
        var form = MainForm;
        if (form is null || form.IsDisposed || !form.IsHandleCreated)
        {
            action();
            return;
        }

        if (!form.InvokeRequired)
        {
            action();
            return;
        }

        form.Invoke(action);
    }
}
