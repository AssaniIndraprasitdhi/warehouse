namespace ElevenX.Warehouse.Services;

public enum ToastType { Success, Error, Warning, Info }

public record ToastMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Message { get; init; } = "";
    public string? Title { get; init; }
    public ToastType Type { get; init; } = ToastType.Success;
    public int Duration { get; init; } = 3500; // มิลลิวินาที (0 = ค้างไว้จนกดปิด)
}

/// <summary>
/// บริการแจ้งเตือน Toast — scoped ต่อ circuit (ผู้ใช้แต่ละคนแยกกัน)
/// เรียกจากหน้าใดก็ได้หลังทำ action สำเร็จ เช่น <c>Toasts.Success("บันทึกสำเร็จ")</c>
/// แล้ว <see cref="Components.Shared.ToastHost"/> ใน MainLayout จะเรนเดอร์ที่มุมขวาบน
/// </summary>
public class ToastService
{
    public event Action<ToastMessage>? OnShow;

    public void Show(string message, ToastType type = ToastType.Success, string? title = null, int duration = 3500)
        => OnShow?.Invoke(new ToastMessage { Message = message, Type = type, Title = title, Duration = duration });

    public void Success(string message, string? title = null) => Show(message, ToastType.Success, title);
    public void Error(string message, string? title = null) => Show(message, ToastType.Error, title, 5000);
    public void Warning(string message, string? title = null) => Show(message, ToastType.Warning, title, 4500);
    public void Info(string message, string? title = null) => Show(message, ToastType.Info, title);
}
